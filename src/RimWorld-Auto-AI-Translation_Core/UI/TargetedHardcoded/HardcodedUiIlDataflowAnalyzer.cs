using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal sealed class HardcodedUiIlAnalysisResult
    {
        internal readonly Dictionary<string, HardcodedUiDecisionRecord> Decisions =
            new Dictionary<string, HardcodedUiDecisionRecord>(StringComparer.Ordinal);
        internal readonly List<string> Diagnostics = new List<string>();
    }

    internal sealed class HardcodedUiIlAnalysisProgress
    {
        internal double OverallRatio { get; set; }
        internal int StageIndex { get; set; }
        internal int StageCount { get; set; }
        internal int Completed { get; set; }
        internal int Total { get; set; }
        internal string Phase { get; set; } = string.Empty;
    }

    internal static class HardcodedUiIlDataflowAnalyzer
    {
        internal const int AnalyzerVersion = 7;
        internal const int StructureEngineVersion = 2;
        private const int MaximumSummaryIterations = 8;
        private const int ProgressStageCount = 5;

        private sealed class AnalysisProgressReporter
        {
            private readonly Action<int, int, string> legacy;
            private readonly Action<HardcodedUiIlAnalysisProgress> structured;
            private double lastOverallRatio;

            internal AnalysisProgressReporter(
                Action<int, int, string> legacy,
                Action<HardcodedUiIlAnalysisProgress> structured)
            {
                this.legacy = legacy;
                this.structured = structured;
            }

            internal void Report(
                int stageIndex,
                double stageStart,
                double stageEnd,
                int completed,
                int total,
                string phase)
            {
                double fraction = total > 0
                    ? Math.Max(0d, Math.Min(1d, (double)completed / total))
                    : 0d;
                ReportAbsolute(
                    stageIndex,
                    stageStart + ((stageEnd - stageStart) * fraction),
                    completed,
                    total,
                    phase);
            }

            internal void ReportAbsolute(
                int stageIndex,
                double overallRatio,
                int completed,
                int total,
                string phase)
            {
                double monotonic = Math.Max(
                    lastOverallRatio,
                    Math.Max(0d, Math.Min(1d, overallRatio)));
                lastOverallRatio = monotonic;
                legacy?.Invoke(completed, total, phase);
                structured?.Invoke(new HardcodedUiIlAnalysisProgress
                {
                    OverallRatio = monotonic,
                    StageIndex = stageIndex,
                    StageCount = ProgressStageCount,
                    Completed = Math.Max(0, completed),
                    Total = Math.Max(0, total),
                    Phase = phase ?? string.Empty
                });
            }
        }

        private sealed class Value
        {
            internal readonly HashSet<string> Literals = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<int> Parameters = new HashSet<int>();
            internal readonly HashSet<string> Containers = new HashSet<string>(StringComparer.Ordinal);

            internal Value Clone()
            {
                var clone = new Value();
                clone.Literals.UnionWith(Literals);
                clone.Parameters.UnionWith(Parameters);
                clone.Containers.UnionWith(Containers);
                return clone;
            }

            internal bool Merge(Value other)
            {
                if (other == null) return false;
                int before = Literals.Count + Parameters.Count + Containers.Count;
                Literals.UnionWith(other.Literals);
                Parameters.UnionWith(other.Parameters);
                Containers.UnionWith(other.Containers);
                return before != Literals.Count + Parameters.Count + Containers.Count;
            }

            internal static Value Union(IEnumerable<Value> values)
            {
                var result = new Value();
                foreach (Value value in values ?? Enumerable.Empty<Value>()) result.Merge(value);
                return result;
            }
        }

        private sealed class State
        {
            internal readonly List<Value> Stack = new List<Value>();
            internal readonly Dictionary<int, Value> Locals = new Dictionary<int, Value>();
            internal readonly Dictionary<string, Value> Heap =
                new Dictionary<string, Value>(StringComparer.Ordinal);

            internal State Clone()
            {
                var clone = new State();
                clone.Stack.AddRange(Stack.Select(value => value.Clone()));
                foreach (KeyValuePair<int, Value> pair in Locals)
                    clone.Locals[pair.Key] = pair.Value.Clone();
                foreach (KeyValuePair<string, Value> pair in Heap)
                    clone.Heap[pair.Key] = pair.Value.Clone();
                return clone;
            }

            internal bool Merge(State other)
            {
                if (other == null) return false;
                bool changed = false;
                if (Stack.Count != other.Stack.Count)
                {
                    int common = Math.Min(Stack.Count, other.Stack.Count);
                    if (Stack.Count != common)
                    {
                        Stack.RemoveRange(common, Stack.Count - common);
                        changed = true;
                    }
                    for (int index = 0; index < common; index++)
                        changed |= Stack[index].Merge(other.Stack[index]);
                }
                else
                {
                    for (int index = 0; index < Stack.Count; index++)
                        changed |= Stack[index].Merge(other.Stack[index]);
                }

                foreach (KeyValuePair<int, Value> pair in other.Locals)
                {
                    if (!Locals.TryGetValue(pair.Key, out Value current))
                    {
                        Locals[pair.Key] = pair.Value.Clone();
                        changed = true;
                    }
                    else changed |= current.Merge(pair.Value);
                }
                foreach (KeyValuePair<string, Value> pair in other.Heap)
                {
                    if (!Heap.TryGetValue(pair.Key, out Value current))
                    {
                        Heap[pair.Key] = pair.Value.Clone();
                        changed = true;
                    }
                    else changed |= current.Merge(pair.Value);
                }
                return changed;
            }
        }

        private sealed class AnalysisContext
        {
            internal readonly Dictionary<string, Value> Fields =
                new Dictionary<string, Value>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Value> Heap =
                new Dictionary<string, Value>(StringComparer.Ordinal);
            internal long Revision { get; private set; }

            internal void StoreField(FieldReference field, Value value, State state)
            {
                if (field == null || value == null) return;
                var persisted = value.Clone();
                // Parameter ordinals are meaningful only inside the method being analyzed.
                // Literal and allocation identities are assembly-wide and may safely cross
                // a field boundary.
                persisted.Parameters.Clear();
                if (MergeValue(Fields, field.FullName, persisted)) Revision++;
                PersistContainers(persisted, state);
            }

            internal Value LoadField(FieldReference field, State state)
            {
                if (field == null || !Fields.TryGetValue(field.FullName, out Value stored))
                    return new Value();
                var loaded = stored.Clone();
                RestoreContainers(loaded, state);
                return loaded;
            }

            internal void MergeContainer(string containerId, Value value)
            {
                if (string.IsNullOrEmpty(containerId) || value == null) return;
                var persisted = value.Clone();
                persisted.Parameters.Clear();
                if (MergeValue(Heap, containerId, persisted)) Revision++;
            }

            internal bool TryGetContainer(string containerId, out Value value)
            {
                return Heap.TryGetValue(containerId ?? string.Empty, out value);
            }

            private void PersistContainers(Value root, State state)
            {
                var pending = new Queue<string>(root.Containers);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pending.Count > 0)
                {
                    string containerId = pending.Dequeue();
                    if (!visited.Add(containerId) ||
                        !state.Heap.TryGetValue(containerId, out Value contents)) continue;
                    MergeContainer(containerId, contents);
                    foreach (string nested in contents.Containers) pending.Enqueue(nested);
                }
            }

            private void RestoreContainers(Value root, State state)
            {
                var pending = new Queue<string>(root.Containers);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pending.Count > 0)
                {
                    string containerId = pending.Dequeue();
                    if (!visited.Add(containerId) || !Heap.TryGetValue(containerId, out Value contents))
                        continue;
                    state.Heap[containerId] = contents.Clone();
                    foreach (string nested in contents.Containers) pending.Enqueue(nested);
                }
            }

            private static bool MergeValue(
                IDictionary<string, Value> target,
                string key,
                Value value)
            {
                if (!target.TryGetValue(key, out Value current))
                {
                    target[key] = value.Clone();
                    return value.Literals.Count > 0 || value.Containers.Count > 0;
                }
                return current.Merge(value);
            }

        }

        private sealed class MethodSummary
        {
            internal readonly Dictionary<int, HashSet<string>> UiRolesByParameter =
                new Dictionary<int, HashSet<string>>();
            internal readonly Dictionary<int, HashSet<string>> NonUiReasonsByParameter =
                new Dictionary<int, HashSet<string>>();
            internal readonly HashSet<int> ReturnParameters = new HashSet<int>();
            internal readonly HashSet<string> ReturnLiterals =
                new HashSet<string>(StringComparer.Ordinal);

            internal bool Merge(MethodSummary other)
            {
                if (other == null) return false;
                bool changed = false;
                changed |= MergeMap(UiRolesByParameter, other.UiRolesByParameter);
                changed |= MergeMap(NonUiReasonsByParameter, other.NonUiReasonsByParameter);
                int before = ReturnParameters.Count;
                ReturnParameters.UnionWith(other.ReturnParameters);
                changed |= before != ReturnParameters.Count;
                before = ReturnLiterals.Count;
                ReturnLiterals.UnionWith(other.ReturnLiterals);
                return changed || before != ReturnLiterals.Count;
            }

            private static bool MergeMap(
                Dictionary<int, HashSet<string>> target,
                Dictionary<int, HashSet<string>> source)
            {
                bool changed = false;
                foreach (KeyValuePair<int, HashSet<string>> pair in source)
                {
                    if (!target.TryGetValue(pair.Key, out HashSet<string> values))
                    {
                        values = new HashSet<string>(StringComparer.Ordinal);
                        target[pair.Key] = values;
                    }
                    int before = values.Count;
                    values.UnionWith(pair.Value);
                    changed |= before != values.Count;
                }
                return changed;
            }
        }

        private sealed class LiteralFact
        {
            internal readonly HashSet<string> UiRoles = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> NonUiReasons = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<string> Evidence = new List<string>();
        }

        private sealed class MethodAnalysis
        {
            internal readonly MethodSummary Summary = new MethodSummary();
            internal readonly Dictionary<string, LiteralFact> Literals =
                new Dictionary<string, LiteralFact>(StringComparer.Ordinal);
            internal readonly List<string> Diagnostics = new List<string>();
        }

        /// <summary>
        /// Immutable for the lifetime of one Cecil assembly load. This deliberately is not
        /// persisted: dependency resolution is influenced by the currently loaded game and
        /// Mod assemblies, so an on-disk cache would need a complete dependency fingerprint.
        /// </summary>
        private sealed class AssemblyStructureIndex
        {
            internal readonly IList<MethodDefinition> AllMethods;
            internal readonly IDictionary<string, MethodDefinition> MethodsByName;
            internal readonly IDictionary<string, MethodDefinition> MethodsByKey;
            internal readonly IDictionary<int, MethodDefinition> MethodsByToken;
            internal readonly IDictionary<string, string[]> Neighbours;
            internal readonly ISet<string> SeedMethods;
            internal readonly ISet<string> CandidateMethods;
            internal readonly ISet<string> UnsafeBoundaryMethods;
            internal readonly string AssemblyMvid;
            internal readonly bool ForceFullAssembly;
            internal readonly string ForceFullAssemblyReason;

            internal AssemblyStructureIndex(
                IList<MethodDefinition> allMethods,
                IDictionary<string, MethodDefinition> methodsByName,
                IDictionary<string, MethodDefinition> methodsByKey,
                IDictionary<int, MethodDefinition> methodsByToken,
                IDictionary<string, string[]> neighbours,
                ISet<string> seedMethods,
                ISet<string> candidateMethods,
                ISet<string> unsafeBoundaryMethods,
                string assemblyMvid,
                bool forceFullAssembly,
                string forceFullAssemblyReason)
            {
                AllMethods = allMethods;
                MethodsByName = methodsByName;
                MethodsByKey = methodsByKey;
                MethodsByToken = methodsByToken;
                Neighbours = neighbours;
                SeedMethods = seedMethods;
                CandidateMethods = candidateMethods;
                UnsafeBoundaryMethods = unsafeBoundaryMethods;
                AssemblyMvid = assemblyMvid ?? string.Empty;
                ForceFullAssembly = forceFullAssembly;
                ForceFullAssemblyReason = forceFullAssemblyReason ?? string.Empty;
            }
        }

        internal static HardcodedUiIlAnalysisResult Analyze(
            string assemblyPath,
            IEnumerable<HardcodedUiPatchEntry> entries,
            IDictionary<string, HardcodedUiDecisionRecord> existingDecisions = null,
            Action<int, int, string> reportProgress = null)
        {
            return Analyze(
                assemblyPath,
                entries,
                existingDecisions,
                reportProgress,
                CancellationToken.None);
        }

        internal static HardcodedUiIlAnalysisResult Analyze(
            string assemblyPath,
            IEnumerable<HardcodedUiPatchEntry> entries,
            IDictionary<string, HardcodedUiDecisionRecord> existingDecisions,
            Action<int, int, string> reportProgress,
            CancellationToken cancellationToken)
        {
            return Analyze(
                assemblyPath,
                entries,
                existingDecisions,
                reportProgress,
                null,
                cancellationToken);
        }

        internal static HardcodedUiIlAnalysisResult Analyze(
            string assemblyPath,
            IEnumerable<HardcodedUiPatchEntry> entries,
            IDictionary<string, HardcodedUiDecisionRecord> existingDecisions,
            Action<int, int, string> reportProgress,
            Action<HardcodedUiIlAnalysisProgress> reportStructuredProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = new AnalysisProgressReporter(
                reportProgress,
                reportStructuredProgress);
            var output = new HardcodedUiIlAnalysisResult();
            List<HardcodedUiPatchEntry> materialized = (entries ?? Enumerable.Empty<HardcodedUiPatchEntry>())
                .Where(entry => entry != null)
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "Cecil input assembly disappeared after runtime scanning. " +
                    "The current Mod analysis must be retried instead of saving baseline decisions.",
                    assemblyPath);

            progress.ReportAbsolute(
                1,
                0d,
                0,
                1,
                AutoTranslatorMod.WfText("验证程序集身份", "validating assembly identity"));
            string diskShaBeforeRead = HardcodedUiMethodIdentity.ComputeFileSha256(assemblyPath);

            using (var resolver = CreateResolver(assemblyPath))
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
                       assemblyPath,
                       new ReaderParameters
                       {
                           ReadingMode = ReadingMode.Immediate,
                           ReadSymbols = false,
                           AssemblyResolver = resolver
                       }))
            {
                string diskShaAfterRead = HardcodedUiMethodIdentity.ComputeFileSha256(assemblyPath);
                ValidateAssemblyIdentity(
                    assemblyPath,
                    assembly,
                    materialized,
                    diskShaBeforeRead,
                    diskShaAfterRead);
                cancellationToken.ThrowIfCancellationRequested();
                List<MethodDefinition> methods = assembly.Modules
                    .SelectMany(module => EnumerateTypes(module.Types))
                    .SelectMany(type => type.Methods)
                    .Where(method => method != null && method.HasBody)
                    .ToList();
                cancellationToken.ThrowIfCancellationRequested();
                AssemblyStructureIndex structure = BuildStructureIndex(
                    assembly,
                    methods,
                    materialized,
                    (completed, total, phase) => progress.Report(
                        1, 0d, 0.12d, completed, total, phase),
                    cancellationToken);
                List<MethodDefinition> relevantMethods = SelectRelevantMethods(
                    structure,
                    out bool closureFallback,
                    out int closureSeedCount,
                    out string closureFallbackReason,
                    (completed, total, phase) => progress.Report(
                        2, 0.12d, 0.15d, completed, total, phase),
                    cancellationToken);
                var relevantMethodKeys = new HashSet<string>(
                    relevantMethods.Select(CreateMethodKey),
                    StringComparer.Ordinal);
                Dictionary<string, MethodDefinition> methodsByName = structure.MethodsByName
                    .Where(pair => relevantMethodKeys.Contains(CreateMethodKey(pair.Value)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var summaries = relevantMethods.ToDictionary(
                    CreateMethodKey,
                    method => new MethodSummary(),
                    StringComparer.Ordinal);
                var context = new AnalysisContext();
                output.Diagnostics.Add(
                    "Cecil structure engine " + StructureEngineVersion +
                    "; MVID=" + structure.AssemblyMvid +
                    "; seeds=" + closureSeedCount.ToString(CultureInfo.InvariantCulture) +
                    "; methods=" + relevantMethods.Count.ToString(CultureInfo.InvariantCulture) +
                    "/" + methods.Count.ToString(CultureInfo.InvariantCulture) +
                    "; unresolved-boundaries=" +
                    structure.UnsafeBoundaryMethods.Count.ToString(CultureInfo.InvariantCulture) +
                    (closureFallback
                        ? "; conservative full-assembly fallback: " + closureFallbackReason
                        : "; dependency closure"));

                for (int iteration = 0; iteration < MaximumSummaryIterations; iteration++)
                {
                    bool changed = false;
                    long contextRevision = context.Revision;
                    int processedMethods = 0;
                    string summaryPhase = ProgressText(
                        "摘要传播第 ", "summary pass ", iteration + 1);
                    int currentIteration = iteration;
                    Action<int, int, string> reportSummaryProgress = (completed, total, phase) =>
                    {
                        double fraction = total > 0
                            ? Math.Max(0d, Math.Min(1d, (double)completed / total))
                            : 0d;
                        double overall = 0.15d + (0.63d *
                            ((currentIteration + fraction) / MaximumSummaryIterations));
                        progress.ReportAbsolute(3, overall, completed, total, phase);
                    };
                    reportSummaryProgress(0, relevantMethods.Count, summaryPhase);
                    foreach (MethodDefinition method in relevantMethods)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        MethodAnalysis analysis = AnalyzeMethod(
                            method, summaries, methodsByName, context, false, cancellationToken);
                        changed |= summaries[CreateMethodKey(method)].Merge(analysis.Summary);
                        processedMethods++;
                        ReportThrottledProgress(
                            reportSummaryProgress,
                            processedMethods,
                            relevantMethods.Count,
                            summaryPhase);
                    }
                    changed |= context.Revision != contextRevision;
                    if (!changed) break;
                }
                progress.ReportAbsolute(
                    3,
                    0.78d,
                    relevantMethods.Count,
                    relevantMethods.Count,
                    AutoTranslatorMod.WfText("摘要传播完成", "summary propagation complete"));

                var literalFacts = new Dictionary<string, LiteralFact>(StringComparer.Ordinal);
                int resolvedMethods = 0;
                string literalPhase = AutoTranslatorMod.WfText(
                    "解析字面量", "resolving literals");
                Action<int, int, string> reportLiteralProgress = (completed, total, phase) =>
                    progress.Report(4, 0.78d, 0.95d, completed, total, phase);
                reportLiteralProgress(0, relevantMethods.Count, literalPhase);
                foreach (MethodDefinition method in relevantMethods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MethodAnalysis analysis = AnalyzeMethod(
                        method, summaries, methodsByName, context, true, cancellationToken);
                    foreach (KeyValuePair<string, LiteralFact> pair in analysis.Literals)
                        MergeLiteralFact(literalFacts, pair.Key, pair.Value);
                    output.Diagnostics.AddRange(analysis.Diagnostics.Select(diagnostic =>
                        method.FullName + ": " + diagnostic));
                    resolvedMethods++;
                    ReportThrottledProgress(
                        reportLiteralProgress,
                        resolvedMethods,
                        relevantMethods.Count,
                        literalPhase);
                }
                progress.ReportAbsolute(
                    4,
                    0.95d,
                    relevantMethods.Count,
                    relevantMethods.Count,
                    AutoTranslatorMod.WfText("字面量解析完成", "literal resolution complete"));

                int processedEntries = 0;
                string decisionPhase = AutoTranslatorMod.WfText(
                    "生成分类结果", "building decisions");
                Action<int, int, string> reportDecisionProgress = (completed, total, phase) =>
                    progress.Report(5, 0.95d, 1d, completed, total, phase);
                reportDecisionProgress(0, materialized.Count, decisionPhase);
                foreach (HardcodedUiPatchEntry entry in materialized)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HardcodedUiDecisionRecord existing = null;
                    existingDecisions?.TryGetValue(entry.EntryId, out existing);
                    HardcodedUiDecisionRecord record = existing?.Clone() ?? new HardcodedUiDecisionRecord
                    {
                        EntryId = entry.EntryId,
                        PackageId = entry.PackageId
                    };
                    string fingerprint = HardcodedUiDecisionRecord.CreateAnalysisInputFingerprint(entry);
                    string literalId = CreateLiteralId(entry.MethodMetadataToken, entry.LiteralOrdinal);
                    if (!literalFacts.TryGetValue(literalId, out LiteralFact fact))
                    {
                        record.SetAutomaticDecision(
                            HardcodedUiAutomaticDecision.Uncertain,
                            "UNKNOWN_ANALYSIS_GAP",
                            AnalyzerVersion,
                            fingerprint,
                            string.Empty,
                            0f,
                            entry.MethodSignature);
                        AddFlag(record, "cecil_literal_not_resolved");
                    }
                    else if (fact.UiRoles.Count > 0 && fact.NonUiReasons.Count == 0)
                    {
                        string role = fact.UiRoles.OrderBy(value => value, StringComparer.Ordinal).First();
                        record.SetAutomaticDecision(
                            HardcodedUiAutomaticDecision.Translate,
                            "UI_DATAFLOW_" + role.ToUpperInvariant(),
                            AnalyzerVersion,
                            fingerprint,
                            role,
                            1f,
                            string.Join("; ", fact.Evidence.Take(8)));
                    }
                    else if (fact.NonUiReasons.Count > 0 && fact.UiRoles.Count == 0)
                    {
                        string reason = fact.NonUiReasons.OrderBy(value => value, StringComparer.Ordinal).First();
                        record.SetAutomaticDecision(
                            HardcodedUiAutomaticDecision.DoNotTranslate,
                            reason,
                            AnalyzerVersion,
                            fingerprint,
                            string.Empty,
                            1f,
                            string.Join("; ", fact.Evidence.Take(8)));
                    }
                    else
                    {
                        record.SetAutomaticDecision(
                            HardcodedUiAutomaticDecision.Uncertain,
                            fact.UiRoles.Count > 0 && fact.NonUiReasons.Count > 0
                                ? "UNKNOWN_AMBIGUOUS_FLOW"
                                : "UNKNOWN_DYNAMIC_FLOW",
                            AnalyzerVersion,
                            fingerprint,
                            string.Empty,
                            0f,
                            string.Join("; ", fact.Evidence.Take(8)));
                        if (fact.UiRoles.Count > 0 && fact.NonUiReasons.Count > 0)
                            AddFlag(record, "flows_to_ui_and_non_ui");
                    }
                    output.Decisions[entry.EntryId] = record;
                    processedEntries++;
                    ReportThrottledProgress(
                        reportDecisionProgress,
                        processedEntries,
                        materialized.Count,
                        decisionPhase);
                }
                progress.ReportAbsolute(
                    5,
                    1d,
                    materialized.Count,
                    materialized.Count,
                    AutoTranslatorMod.WfText("分类结果完成", "decisions complete"));
            }
            return output;
        }

        private static void ValidateAssemblyIdentity(
            string assemblyPath,
            AssemblyDefinition assembly,
            IEnumerable<HardcodedUiPatchEntry> entries,
            string diskShaBeforeRead,
            string diskShaAfterRead)
        {
            if (!string.Equals(
                    diskShaBeforeRead,
                    diskShaAfterRead,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    AutoTranslatorMod.WfText(
                        "静态分析读取 DLL 时文件发生了变化。请重启 RimWorld 或重新加载该 Mod 后再分析：",
                        "DLL changed while static analysis was opening it. Restart RimWorld or reload the Mod, then analyze again: ") +
                    assemblyPath);

            List<HardcodedUiPatchEntry> materialized = (entries ??
                Enumerable.Empty<HardcodedUiPatchEntry>()).Where(entry => entry != null).ToList();
            if (materialized.Count == 0) return;

            string[] expectedHashes = materialized
                .Select(entry => entry.AssemblySha256 ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] expectedMvids = materialized
                .Select(entry => entry.AssemblyMvid ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (expectedHashes.Length != 1 || string.IsNullOrWhiteSpace(expectedHashes[0]) ||
                expectedMvids.Length != 1 || string.IsNullOrWhiteSpace(expectedMvids[0]))
                throw new InvalidDataException(
                    AutoTranslatorMod.WfText(
                        "运行时扫描条目没有一致且完整的 DLL SHA-256/MVID 标识。请重启 RimWorld 或重新加载该 Mod 后再分析：",
                        "Runtime scan entries do not share one complete DLL SHA-256/MVID identity. Restart RimWorld or reload the Mod, then analyze again: ") +
                    assemblyPath);

            string actualMvid = assembly?.MainModule?.Mvid.ToString("D") ?? string.Empty;
            if (!string.Equals(expectedHashes[0], diskShaAfterRead, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(expectedMvids[0], actualMvid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    AutoTranslatorMod.WfText(
                        "游戏已加载的 DLL 与当前磁盘 DLL 不一致，不能安全复用方法 Token。请重启 RimWorld 或重新加载该 Mod 后再分析：",
                        "The loaded runtime DLL and the current disk DLL are different. Metadata tokens are unsafe to reuse. Restart RimWorld or reload the Mod, then analyze again: ") +
                    assemblyPath);
        }

        private static AssemblyStructureIndex BuildStructureIndex(
            AssemblyDefinition assembly,
            IList<MethodDefinition> methods,
            IEnumerable<HardcodedUiPatchEntry> entries,
            Action<int, int, string> reportProgress,
            CancellationToken cancellationToken)
        {
            string phase = AutoTranslatorMod.WfText(
                "建立依赖索引", "building dependency index");
            reportProgress?.Invoke(0, methods.Count, phase);

            var methodsByName = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
            var methodsByKey = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
            var methodsByToken = new Dictionary<int, MethodDefinition>();
            var neighbours = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var fieldAccessors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var dispatchMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var resolvedMethods = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
            var resolvedFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
            var resolvedTypes = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
            var seeds = new HashSet<string>(StringComparer.Ordinal);
            var candidateMethods = new HashSet<string>(StringComparer.Ordinal);
            var unsafeBoundaries = new HashSet<string>(StringComparer.Ordinal);

            foreach (MethodDefinition method in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!methodsByName.ContainsKey(method.FullName))
                    methodsByName[method.FullName] = method;
                string methodKey = CreateMethodKey(method);
                methodsByKey[methodKey] = method;
                methodsByToken[method.MetadataToken.ToInt32()] = method;
                neighbours[methodKey] = new HashSet<string>(StringComparer.Ordinal);
                AddGroupedMethod(dispatchMethods, CreateDispatchKey(method), methodKey);
            }

            bool forceFullAssembly = false;
            string forceFullAssemblyReason = string.Empty;
            foreach (HardcodedUiPatchEntry entry in entries ?? Enumerable.Empty<HardcodedUiPatchEntry>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry == null) continue;
                if (methodsByToken.TryGetValue(entry.MethodMetadataToken, out MethodDefinition method))
                {
                    string methodKey = CreateMethodKey(method);
                    seeds.Add(methodKey);
                    candidateMethods.Add(methodKey);
                }
                else
                {
                    forceFullAssembly = true;
                    forceFullAssemblyReason = "candidate metadata token was not found";
                }
            }

            int processed = 0;
            foreach (MethodDefinition method in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string methodKey = CreateMethodKey(method);
                int indexedInstructions = 0;
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if ((indexedInstructions++ & 255) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (instruction.Operand is MethodReference called)
                    {
                        bool knownSink = HasKnownSink(called);
                        if (knownSink) seeds.Add(methodKey);
                        if (IsDynamicInvocation(called))
                            unsafeBoundaries.Add(methodKey);

                        MethodDefinition resolved = ResolveMethodCached(called, resolvedMethods);
                        if (resolved != null && IsSameAssembly(resolved.Module?.Assembly, assembly))
                        {
                            if (resolved.HasBody)
                                AddUndirectedEdge(neighbours, methodKey, CreateMethodKey(resolved));
                            else if (!resolved.IsAbstract)
                                unsafeBoundaries.Add(methodKey);
                        }
                        else if (methodsByName.TryGetValue(called.FullName, out MethodDefinition exact))
                        {
                            AddUndirectedEdge(neighbours, methodKey, CreateMethodKey(exact));
                        }
                        else if (IsPotentiallyAssemblyLocal(called.DeclaringType, assembly))
                        {
                            // An unresolved internal edge can hide an arbitrary producer or
                            // consumer. If its caller enters the closure, pruning is disabled.
                            unsafeBoundaries.Add(methodKey);
                        }

                        bool virtualDispatch = (resolved != null && resolved.IsVirtual) ||
                                               IsInterfaceType(called.DeclaringType, resolvedTypes) ||
                                               (resolved == null && instruction.OpCode.Code == Code.Callvirt);
                        if (virtualDispatch && dispatchMethods.TryGetValue(
                                CreateDispatchKey(called), out HashSet<string> implementations))
                        {
                            foreach (string implementation in implementations)
                            {
                                if (methodsByKey.TryGetValue(
                                        implementation, out MethodDefinition candidate) &&
                                    IsPotentialDispatchTarget(called, candidate, resolvedTypes))
                                    AddUndirectedEdge(neighbours, methodKey, implementation);
                            }
                        }
                    }
                    else if (instruction.OpCode.Code == Code.Calli)
                    {
                        unsafeBoundaries.Add(methodKey);
                    }
                    else if (instruction.Operand is FieldReference field)
                    {
                        FieldDefinition resolved = ResolveFieldCached(field, resolvedFields);
                        AddGroupedMethod(
                            fieldAccessors,
                            resolved?.FullName ?? field.FullName,
                            methodKey);
                        if (resolved == null && IsPotentiallyAssemblyLocal(field.DeclaringType, assembly))
                            unsafeBoundaries.Add(methodKey);
                    }
                }

                AddStateMachineEdges(method, assembly, neighbours, unsafeBoundaries);
                processed++;
                ReportThrottledProgress(reportProgress, processed, methods.Count, phase);
            }

            foreach (HashSet<string> accessors in fieldAccessors.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConnectGroup(neighbours, accessors);
            }
            AddGeneratedNestedTypeEdges(methods, neighbours, cancellationToken);

            var frozenNeighbours = neighbours.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
            string mvid = assembly.Modules.FirstOrDefault()?.Mvid.ToString("D") ?? string.Empty;
            return new AssemblyStructureIndex(
                methods.ToList(),
                methodsByName,
                methodsByKey,
                methodsByToken,
                frozenNeighbours,
                seeds,
                candidateMethods,
                unsafeBoundaries,
                mvid,
                forceFullAssembly,
                forceFullAssemblyReason);
        }

        private static List<MethodDefinition> SelectRelevantMethods(
            AssemblyStructureIndex structure,
            out bool fullAssemblyFallback,
            out int seedCount,
            out string fallbackReason,
            Action<int, int, string> reportProgress,
            CancellationToken cancellationToken)
        {
            string phase = AutoTranslatorMod.WfText(
                "选择依赖闭包", "selecting dependency closure");
            reportProgress?.Invoke(0, Math.Max(1, structure.AllMethods.Count), phase);
            seedCount = structure.SeedMethods.Count;
            fullAssemblyFallback = structure.ForceFullAssembly;
            fallbackReason = structure.ForceFullAssemblyReason;
            if (fullAssemblyFallback || seedCount == 0)
            {
                if (string.IsNullOrEmpty(fallbackReason)) fallbackReason = "dependency closure has no seeds";
                reportProgress?.Invoke(1, 1, phase);
                return structure.AllMethods.ToList();
            }
            int unsafeLimit = Math.Max(16, structure.AllMethods.Count / 10);
            if (structure.UnsafeBoundaryMethods.Count > unsafeLimit)
            {
                fullAssemblyFallback = true;
                fallbackReason = "unresolved boundary ratio exceeded the conservative limit";
                reportProgress?.Invoke(1, 1, phase);
                return structure.AllMethods.ToList();
            }

            var included = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(structure.SeedMethods.OrderBy(
                value => value, StringComparer.Ordinal));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string method = pending.Dequeue();
                if (!included.Add(method)) continue;
                ReportThrottledProgress(
                    reportProgress,
                    included.Count,
                    structure.AllMethods.Count,
                    phase);
                if (structure.UnsafeBoundaryMethods.Contains(method))
                {
                    fullAssemblyFallback = true;
                    fallbackReason = "dependency closure reached an unresolved boundary";
                    reportProgress?.Invoke(1, 1, phase);
                    return structure.AllMethods.ToList();
                }
                if (!structure.Neighbours.TryGetValue(method, out string[] neighbours)) continue;
                foreach (string neighbour in neighbours)
                    if (!included.Contains(neighbour)) pending.Enqueue(neighbour);
            }

            // Preserve Cecil metadata order. The existing summary propagation is
            // Gauss-Seidel-like, so deterministic ordering is part of reproducibility.
            if (included.Count == 0)
            {
                fullAssemblyFallback = true;
                fallbackReason = "dependency closure was empty";
                reportProgress?.Invoke(1, 1, phase);
                return structure.AllMethods.ToList();
            }
            if (structure.CandidateMethods.Any(method => !included.Contains(method)))
            {
                fullAssemblyFallback = true;
                fallbackReason = "dependency closure omitted a candidate method";
                reportProgress?.Invoke(1, 1, phase);
                return structure.AllMethods.ToList();
            }
            reportProgress?.Invoke(1, 1, phase);
            return structure.AllMethods.Where(method => included.Contains(CreateMethodKey(method))).ToList();
        }

        private static bool HasKnownSink(MethodReference call)
        {
            if (call == null) return false;
            string type = call.DeclaringType?.FullName ?? string.Empty;
            string name = call.Name ?? string.Empty;
            if (TryGetUiRole(type, name, out string _) ||
                TryGetNonUiReason(type, name, out string _) ||
                TryGetNonUiInstanceReason(call, out string _)) return true;
            for (int index = 0; index < call.Parameters.Count; index++)
                if (TryGetNonUiArgumentReason(call, index, out string _)) return true;
            return false;
        }

        private static bool IsDynamicInvocation(MethodReference call)
        {
            if (call == null) return false;
            string type = call.DeclaringType?.FullName ?? string.Empty;
            string name = call.Name ?? string.Empty;
            if (type == "System.Delegate" && name == "DynamicInvoke") return true;
            if ((type == "System.Reflection.MethodBase" ||
                 type == "System.Reflection.MethodInfo") && name == "Invoke") return true;
            if (name != "Invoke") return false;
            TypeDefinition resolvedType = ResolveTypeSafely(call.DeclaringType);
            while (resolvedType != null)
            {
                if (resolvedType.BaseType?.FullName == "System.MulticastDelegate" && name == "Invoke")
                    return true;
                resolvedType = ResolveTypeSafely(resolvedType.BaseType);
            }
            return false;
        }

        private static void AddStateMachineEdges(
            MethodDefinition method,
            AssemblyDefinition assembly,
            IDictionary<string, HashSet<string>> neighbours,
            ISet<string> unsafeBoundaries)
        {
            string methodKey = CreateMethodKey(method);
            foreach (CustomAttribute attribute in method.CustomAttributes)
            {
                string name = attribute.AttributeType?.FullName ?? string.Empty;
                if (name != "System.Runtime.CompilerServices.AsyncStateMachineAttribute" &&
                    name != "System.Runtime.CompilerServices.IteratorStateMachineAttribute") continue;
                if (attribute.ConstructorArguments.Count == 0 ||
                    !(attribute.ConstructorArguments[0].Value is TypeReference stateMachineType))
                {
                    unsafeBoundaries.Add(methodKey);
                    continue;
                }
                TypeDefinition stateMachine = ResolveTypeSafely(stateMachineType);
                if (stateMachine == null || !IsSameAssembly(stateMachine.Module?.Assembly, assembly))
                {
                    unsafeBoundaries.Add(methodKey);
                    continue;
                }
                foreach (MethodDefinition generated in stateMachine.Methods.Where(item => item.HasBody))
                {
                    AddUndirectedEdge(neighbours, methodKey, CreateMethodKey(generated));
                }
            }
        }

        private static void AddGeneratedNestedTypeEdges(
            IEnumerable<MethodDefinition> methods,
            IDictionary<string, HashSet<string>> neighbours,
            CancellationToken cancellationToken)
        {
            foreach (IGrouping<TypeDefinition, MethodDefinition> group in methods
                         .Where(method => method.DeclaringType != null)
                         .GroupBy(method => GetGeneratedOwner(method.DeclaringType)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Key == null) continue;
                List<string> owners = group.Where(method => method.DeclaringType == group.Key)
                    .Select(CreateMethodKey).ToList();
                List<string> generated = group.Where(method => method.DeclaringType != group.Key &&
                    IsGeneratedType(method.DeclaringType))
                    .Select(CreateMethodKey).ToList();
                if (generated.Count == 0 || owners.Count == 0) continue;
                foreach (string child in generated)
                    AddUndirectedEdge(neighbours, owners[0], child);
                for (int index = 1; index < owners.Count; index++)
                    AddUndirectedEdge(neighbours, owners[index], generated[0]);
            }
        }

        private static TypeDefinition GetGeneratedOwner(TypeDefinition type)
        {
            TypeDefinition current = type;
            while (current?.DeclaringType != null && IsGeneratedType(current))
                current = current.DeclaringType;
            return current;
        }

        private static bool IsGeneratedType(TypeDefinition type)
        {
            if (type == null) return false;
            if ((type.Name ?? string.Empty).IndexOf('<') >= 0) return true;
            return type.CustomAttributes.Any(attribute =>
                attribute.AttributeType?.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        private static void AddGroupedMethod(
            IDictionary<string, HashSet<string>> groups,
            string key,
            string method)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(method)) return;
            if (!groups.TryGetValue(key, out HashSet<string> values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                groups[key] = values;
            }
            values.Add(method);
        }

        private static void ConnectGroup(
            IDictionary<string, HashSet<string>> neighbours,
            IEnumerable<string> members)
        {
            string[] ordered = members.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (ordered.Length < 2) return;
            // A star has the same undirected reachability as a clique without O(n^2) edges.
            for (int index = 1; index < ordered.Length; index++)
                AddUndirectedEdge(neighbours, ordered[0], ordered[index]);
        }

        private static void AddUndirectedEdge(
            IDictionary<string, HashSet<string>> neighbours,
            string left,
            string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || left == right) return;
            if (!neighbours.TryGetValue(left, out HashSet<string> leftValues) ||
                !neighbours.TryGetValue(right, out HashSet<string> rightValues)) return;
            leftValues.Add(right);
            rightValues.Add(left);
        }

        private static string CreateDispatchKey(MethodReference method)
        {
            if (method == null) return string.Empty;
            return (method.Name ?? string.Empty) + "#" +
                   method.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        }

        private static string CreateMethodKey(MethodDefinition method)
        {
            if (method == null) return string.Empty;
            string moduleIdentity = method.Module?.Mvid.ToString("D") ?? string.Empty;
            int metadataToken = method.MetadataToken.ToInt32();
            if (!string.IsNullOrEmpty(moduleIdentity) && metadataToken != 0)
                return moduleIdentity + ":" + metadataToken.ToString("x8", CultureInfo.InvariantCulture);
            return (method.Module?.Name ?? string.Empty) + ":" + (method.FullName ?? string.Empty);
        }

        private static bool TryGetMethodSummary(
            MethodReference method,
            IDictionary<string, MethodSummary> summaries,
            IDictionary<string, MethodDefinition> methodsByName,
            out MethodSummary summary)
        {
            summary = null;
            if (method == null || summaries == null) return false;
            MethodDefinition resolved = ResolveMethodSafely(method);
            if (resolved != null && summaries.TryGetValue(CreateMethodKey(resolved), out summary))
                return true;
            if (methodsByName != null &&
                methodsByName.TryGetValue(method.FullName, out MethodDefinition exact))
                return summaries.TryGetValue(CreateMethodKey(exact), out summary);
            return false;
        }

        private static MethodDefinition ResolveMethodSafely(MethodReference method)
        {
            try { return method?.Resolve(); }
            catch { return null; }
        }

        private static MethodDefinition ResolveMethodCached(
            MethodReference method,
            IDictionary<string, MethodDefinition> cache)
        {
            string key = CreateMethodReferenceKey(method);
            if (cache.TryGetValue(key, out MethodDefinition resolved)) return resolved;
            resolved = ResolveMethodSafely(method);
            cache[key] = resolved;
            return resolved;
        }

        private static string CreateMethodReferenceKey(MethodReference method)
        {
            if (method == null) return string.Empty;
            string moduleIdentity = method.Module?.Mvid.ToString("D") ?? string.Empty;
            int metadataToken = method.MetadataToken.ToInt32();
            return moduleIdentity + ":" + metadataToken.ToString("x8", CultureInfo.InvariantCulture) + "|" +
                   CreateResolutionKey(method.DeclaringType, method.FullName);
        }

        private static FieldDefinition ResolveFieldSafely(FieldReference field)
        {
            try { return field?.Resolve(); }
            catch { return null; }
        }

        private static FieldDefinition ResolveFieldCached(
            FieldReference field,
            IDictionary<string, FieldDefinition> cache)
        {
            string key = CreateResolutionKey(field?.DeclaringType, field?.FullName);
            if (cache.TryGetValue(key, out FieldDefinition resolved)) return resolved;
            resolved = ResolveFieldSafely(field);
            cache[key] = resolved;
            return resolved;
        }

        private static TypeDefinition ResolveTypeSafely(TypeReference type)
        {
            try { return type?.Resolve(); }
            catch { return null; }
        }

        private static TypeDefinition ResolveTypeCached(
            TypeReference type,
            IDictionary<string, TypeDefinition> cache)
        {
            string key = CreateResolutionKey(type, type?.FullName);
            if (cache.TryGetValue(key, out TypeDefinition resolved)) return resolved;
            resolved = ResolveTypeSafely(type);
            cache[key] = resolved;
            return resolved;
        }

        private static string CreateResolutionKey(TypeReference declaringType, string member)
        {
            return (declaringType?.Scope?.Name ?? string.Empty) + "|" + (member ?? string.Empty);
        }

        private static bool IsInterfaceType(
            TypeReference type,
            IDictionary<string, TypeDefinition> typeCache)
        {
            TypeDefinition resolved = ResolveTypeCached(type, typeCache);
            return resolved != null && resolved.IsInterface;
        }

        private static bool IsPotentialDispatchTarget(
            MethodReference called,
            MethodDefinition candidate,
            IDictionary<string, TypeDefinition> typeCache)
        {
            if (called == null || candidate?.DeclaringType == null) return false;
            TypeDefinition targetType = ResolveTypeCached(called.DeclaringType, typeCache);
            if (targetType == null)
            {
                // Resolution failed, so retain all signature-compatible local targets.
                return true;
            }
            if (string.Equals(
                    candidate.DeclaringType.FullName,
                    targetType.FullName,
                    StringComparison.Ordinal)) return true;
            return TypeDerivesFrom(candidate.DeclaringType, targetType.FullName, typeCache);
        }

        private static bool TypeDerivesFrom(
            TypeDefinition type,
            string targetTypeName,
            IDictionary<string, TypeDefinition> typeCache)
        {
            var pending = new Queue<TypeReference>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            if (type?.BaseType != null) pending.Enqueue(type.BaseType);
            if (type != null)
                foreach (InterfaceImplementation implementation in type.Interfaces)
                    if (implementation?.InterfaceType != null)
                        pending.Enqueue(implementation.InterfaceType);
            while (pending.Count > 0)
            {
                TypeReference current = pending.Dequeue();
                string name = current?.FullName ?? string.Empty;
                if (!visited.Add(name)) continue;
                if (string.Equals(name, targetTypeName, StringComparison.Ordinal)) return true;
                TypeDefinition resolved = ResolveTypeCached(current, typeCache);
                if (resolved?.BaseType != null) pending.Enqueue(resolved.BaseType);
                if (resolved != null)
                    foreach (InterfaceImplementation implementation in resolved.Interfaces)
                        if (implementation?.InterfaceType != null)
                            pending.Enqueue(implementation.InterfaceType);
            }
            return false;
        }

        private static bool IsSameAssembly(AssemblyDefinition left, AssemblyDefinition right)
        {
            if (left == null || right == null) return false;
            return ReferenceEquals(left, right) ||
                   string.Equals(left.Name?.FullName, right.Name?.FullName, StringComparison.Ordinal);
        }

        private static bool IsPotentiallyAssemblyLocal(
            TypeReference declaringType,
            AssemblyDefinition assembly)
        {
            if (declaringType == null || assembly == null) return false;
            TypeDefinition resolved = ResolveTypeSafely(declaringType);
            if (resolved != null) return IsSameAssembly(resolved.Module?.Assembly, assembly);
            string scopeName = declaringType.Scope?.Name ?? string.Empty;
            return assembly.Modules.Any(module =>
                       string.Equals(module.Name, scopeName, StringComparison.OrdinalIgnoreCase)) ||
                   string.Equals(assembly.Name?.Name, scopeName, StringComparison.OrdinalIgnoreCase);
        }

        private static string ProgressText(string chinesePrefix, string englishPrefix, int iteration)
        {
            return AutoTranslatorMod.WfText(
                chinesePrefix + iteration + " 轮",
                englishPrefix + iteration);
        }

        private static void ReportThrottledProgress(
            Action<int, int, string> reportProgress,
            int completed,
            int total,
            string phase)
        {
            if (reportProgress == null) return;
            int safeTotal = Math.Max(0, total);
            int interval = Math.Max(1, safeTotal / 100);
            if (completed < safeTotal && completed % interval != 0) return;
            reportProgress(completed, safeTotal, phase);
        }

        private static MethodAnalysis AnalyzeMethod(
            MethodDefinition method,
            IDictionary<string, MethodSummary> summaries,
            IDictionary<string, MethodDefinition> methodsByName,
            AnalysisContext context,
            bool collectLiterals,
            CancellationToken cancellationToken)
        {
            var analysis = new MethodAnalysis();
            IList<Instruction> instructions = method.Body.Instructions;
            if (instructions.Count == 0) return analysis;
            var indexes = instructions.Select((instruction, index) => new { instruction, index })
                .ToDictionary(pair => pair.instruction, pair => pair.index);
            var incoming = new State[instructions.Count];
            incoming[0] = new State();
            var work = new Queue<int>();
            var queued = new HashSet<int>();
            work.Enqueue(0);
            queued.Add(0);
            int literalOrdinal = -1;
            var literalOrdinals = new Dictionary<Instruction, int>();
            foreach (Instruction instruction in instructions)
            {
                if (instruction.OpCode.Code == Code.Ldstr)
                    literalOrdinals[instruction] = ++literalOrdinal;
            }

            int safety = 0;
            while (work.Count > 0 && safety++ < instructions.Count * 128)
            {
                if ((safety & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                int index = work.Dequeue();
                queued.Remove(index);
                State state = incoming[index].Clone();
                Instruction instruction = instructions[index];
                try
                {
                    Execute(
                        method,
                        instruction,
                        literalOrdinals,
                        state,
                        analysis,
                        summaries,
                        methodsByName,
                        context,
                        collectLiterals);
                }
                catch (Exception ex)
                {
                    analysis.Diagnostics.Add("IL_" + instruction.Offset.ToString("x4", CultureInfo.InvariantCulture) +
                        " " + instruction.OpCode + ": " + ex.Message);
                    state.Stack.Clear();
                }

                foreach (int successor in GetSuccessors(instructions, indexes, index, instruction))
                {
                    if (successor < 0 || successor >= instructions.Count) continue;
                    bool changed;
                    if (incoming[successor] == null)
                    {
                        incoming[successor] = state.Clone();
                        changed = true;
                    }
                    else changed = incoming[successor].Merge(state);
                    if (changed && queued.Add(successor)) work.Enqueue(successor);
                }
            }
            if (safety >= instructions.Count * 128)
                analysis.Diagnostics.Add("control-flow iteration safety limit reached");
            return analysis;
        }

        private static void Execute(
            MethodDefinition method,
            Instruction instruction,
            IDictionary<Instruction, int> literalOrdinals,
            State state,
            MethodAnalysis analysis,
            IDictionary<string, MethodSummary> summaries,
            IDictionary<string, MethodDefinition> methodsByName,
            AnalysisContext context,
            bool collectLiterals)
        {
            Code code = instruction.OpCode.Code;
            if (code == Code.Ldstr)
            {
                var value = new Value();
                string literalId = CreateLiteralId(
                    method.MetadataToken.ToInt32(), literalOrdinals[instruction]);
                value.Literals.Add(literalId);
                state.Stack.Add(value);
                if (collectLiterals && !analysis.Literals.ContainsKey(literalId))
                    analysis.Literals[literalId] = new LiteralFact();
                return;
            }

            if (TryGetArgumentIndex(method, instruction, out int argumentIndex))
            {
                var value = new Value();
                if (argumentIndex >= 0) value.Parameters.Add(argumentIndex);
                state.Stack.Add(value);
                return;
            }
            if (TryGetLocalIndex(instruction, true, out int loadLocal))
            {
                state.Stack.Add(state.Locals.TryGetValue(loadLocal, out Value value)
                    ? value.Clone()
                    : new Value());
                return;
            }
            if (TryGetLocalIndex(instruction, false, out int storeLocal))
            {
                state.Locals[storeLocal] = Pop(state).Clone();
                return;
            }

            if (code == Code.Stsfld && instruction.Operand is FieldReference storeField)
            {
                Value stored = Pop(state);
                context.StoreField(storeField, stored, state);
                return;
            }
            if (code == Code.Ldsfld && instruction.Operand is FieldReference loadField)
            {
                state.Stack.Add(context.LoadField(loadField, state));
                return;
            }
            if (code == Code.Dup)
            {
                state.Stack.Add(state.Stack.Count > 0 ? state.Stack[state.Stack.Count - 1].Clone() : new Value());
                return;
            }
            if (code == Code.Pop) { Pop(state); return; }
            if (code == Code.Newarr)
            {
                Pop(state);
                string containerId = CreateAllocationId(method, instruction);
                var array = new Value();
                array.Containers.Add(containerId);
                state.Heap[containerId] = new Value();
                state.Stack.Add(array);
                return;
            }
            if (IsStoreElement(code))
            {
                Value element = Pop(state);
                Pop(state);
                Value array = Pop(state);
                MergeIntoContainers(state, context, array, element);
                return;
            }
            if (IsLoadElement(code))
            {
                Pop(state);
                Value array = Pop(state);
                state.Stack.Add(ExpandContainers(state, context, array));
                return;
            }
            if (code == Code.Ldlen)
            {
                Pop(state);
                state.Stack.Add(new Value());
                return;
            }
            if (code == Code.Ret)
            {
                if (method.ReturnType.MetadataType != MetadataType.Void)
                {
                    Value returned = ExpandContainers(state, context, Pop(state));
                    analysis.Summary.ReturnParameters.UnionWith(returned.Parameters);
                    analysis.Summary.ReturnLiterals.UnionWith(returned.Literals);
                }
                return;
            }
            if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj)
            {
                ExecuteCall(
                    method, instruction, state, analysis, summaries, methodsByName, context, collectLiterals);
                return;
            }
            if (code == Code.Leave || code == Code.Leave_S)
            {
                state.Stack.Clear();
                return;
            }

            ExecuteGenericStackBehaviour(instruction.OpCode, state);
        }

        private static void ExecuteCall(
            MethodDefinition method,
            Instruction instruction,
            State state,
            MethodAnalysis analysis,
            IDictionary<string, MethodSummary> summaries,
            IDictionary<string, MethodDefinition> methodsByName,
            AnalysisContext context,
            bool collectLiterals)
        {
            MethodReference call = instruction.Operand as MethodReference;
            if (call == null) { state.Stack.Clear(); return; }
            Value[] arguments = new Value[call.Parameters.Count];
            for (int index = call.Parameters.Count - 1; index >= 0; index--)
                arguments[index] = Pop(state);
            Value instance = call.HasThis && instruction.OpCode.Code != Code.Newobj
                ? Pop(state)
                : new Value();
            string declaringType = call.DeclaringType?.FullName ?? string.Empty;
            string methodName = call.Name ?? string.Empty;
            string evidence = declaringType + "." + methodName;

            if (TryGetUiRole(declaringType, methodName, out string role))
            {
                for (int index = 0; index < arguments.Length; index++)
                {
                    if (IsStringLike(ResolveCallType(call.Parameters[index].ParameterType, call)))
                        Mark(analysis, arguments[index], true, role, evidence, collectLiterals);
                }
            }
            if (TryGetNonUiReason(declaringType, methodName, out string nonUiReason))
            {
                for (int index = 0; index < arguments.Length; index++)
                {
                    if (IsStringLike(ResolveCallType(call.Parameters[index].ParameterType, call)))
                        Mark(analysis, arguments[index], false, nonUiReason, evidence, collectLiterals);
                }
            }
            for (int index = 0; index < arguments.Length; index++)
            {
                if (IsStringLike(ResolveCallType(call.Parameters[index].ParameterType, call)) &&
                    TryGetNonUiArgumentReason(call, index, out string argumentReason))
                    Mark(analysis, arguments[index], false, argumentReason, evidence, collectLiterals);
            }
            if (TryGetNonUiInstanceReason(call, out string instanceReason))
                Mark(analysis, instance, false, instanceReason, evidence, collectLiterals);
            if (IsCollectionMutation(declaringType, methodName, out int collectionValueIndex) &&
                collectionValueIndex >= 0 && collectionValueIndex < arguments.Length)
                MergeIntoContainers(state, context, instance, arguments[collectionValueIndex]);

            if (TryGetMethodSummary(call, summaries, methodsByName, out MethodSummary summary))
            {
                foreach (KeyValuePair<int, HashSet<string>> pair in summary.UiRolesByParameter)
                {
                    if (pair.Key < 0 || pair.Key >= arguments.Length) continue;
                    foreach (string summaryRole in pair.Value)
                        Mark(analysis, arguments[pair.Key], true, summaryRole,
                            evidence + " wrapper", collectLiterals);
                }
                foreach (KeyValuePair<int, HashSet<string>> pair in summary.NonUiReasonsByParameter)
                {
                    if (pair.Key < 0 || pair.Key >= arguments.Length) continue;
                    foreach (string reason in pair.Value)
                        Mark(analysis, arguments[pair.Key], false, reason,
                            evidence + " wrapper", collectLiterals);
                }
            }

            bool hasReturn = instruction.OpCode.Code == Code.Newobj ||
                             call.ReturnType.MetadataType != MetadataType.Void;
            if (!hasReturn) return;
            Value returnValue = new Value();
            if (TryGetMethodSummary(call, summaries, methodsByName, out MethodSummary returnSummary))
            {
                foreach (int parameterIndex in returnSummary.ReturnParameters)
                    if (parameterIndex >= 0 && parameterIndex < arguments.Length)
                        returnValue.Merge(arguments[parameterIndex]);
                returnValue.Literals.UnionWith(returnSummary.ReturnLiterals);
            }
            if (instruction.OpCode.Code == Code.Newobj)
            {
                if (IsContainerType(declaringType))
                {
                    string containerId = CreateAllocationId(method, instruction);
                    returnValue.Containers.Add(containerId);
                    if (!state.Heap.ContainsKey(containerId)) state.Heap[containerId] = new Value();
                }
                else if (methodsByName.ContainsKey(call.FullName))
                {
                    // Carry constructor string origins with same-assembly data objects.
                    // A later string property getter may expose one of them to the UI;
                    // non-UI uses are still recorded independently and make the result
                    // ambiguous rather than forcing translation.
                    returnValue.Merge(Value.Union(arguments));
                }
            }
            if (IsTranslationLookup(declaringType, methodName))
            {
                // The literal is a localization key. The translated return value must not
                // carry that key into a later UI sink, otherwise it becomes an ambiguous
                // "UI and non-UI" result and may be patched before the lookup occurs.
            }
            else if (methodName == "ToString" && call.ReturnType?.FullName == "System.String")
            {
                // The receiver contributes the value; format/provider arguments do not
                // become visible text by themselves.
                returnValue.Merge(instance);
            }
            else if (IsKnownStringPropagation(declaringType, methodName, call.ReturnType))
            {
                returnValue.Merge(instance);
                returnValue.Merge(Value.Union(arguments));
            }
            else if (IsEnumerablePropagation(declaringType, methodName, call.ReturnType))
            {
                foreach (Value argument in arguments)
                    returnValue.Merge(ExpandContainers(state, context, argument));
            }
            else if (IsContainerRead(declaringType, methodName))
            {
                returnValue.Merge(ExpandContainers(state, context, instance));
                returnValue.Containers.UnionWith(instance.Containers);
            }
            else if (methodName.StartsWith("get_", StringComparison.Ordinal) &&
                     IsStringLike(ResolveCallType(call.ReturnType, call)) && methodsByName.ContainsKey(call.FullName))
            {
                returnValue.Merge(ExpandContainers(state, context, instance));
            }
            state.Stack.Add(returnValue);
        }

        private static void Mark(
            MethodAnalysis analysis,
            Value value,
            bool isUi,
            string reason,
            string evidence,
            bool collectLiterals)
        {
            foreach (int parameter in value.Parameters)
            {
                Dictionary<int, HashSet<string>> map = isUi
                    ? analysis.Summary.UiRolesByParameter
                    : analysis.Summary.NonUiReasonsByParameter;
                if (!map.TryGetValue(parameter, out HashSet<string> reasons))
                {
                    reasons = new HashSet<string>(StringComparer.Ordinal);
                    map[parameter] = reasons;
                }
                reasons.Add(reason);
            }
            if (!collectLiterals) return;
            foreach (string literal in value.Literals)
            {
                if (!analysis.Literals.TryGetValue(literal, out LiteralFact fact))
                {
                    fact = new LiteralFact();
                    analysis.Literals[literal] = fact;
                }
                if (isUi) fact.UiRoles.Add(reason);
                else fact.NonUiReasons.Add(reason);
                if (fact.Evidence.Count < 16 && !fact.Evidence.Contains(evidence))
                    fact.Evidence.Add(evidence);
            }
        }

        private static IEnumerable<int> GetSuccessors(
            IList<Instruction> instructions,
            IDictionary<Instruction, int> indexes,
            int index,
            Instruction instruction)
        {
            Code code = instruction.OpCode.Code;
            if (code == Code.Ret || code == Code.Throw || code == Code.Rethrow || code == Code.Endfinally)
                yield break;
            if (instruction.Operand is Instruction target)
            {
                if (indexes.TryGetValue(target, out int targetIndex)) yield return targetIndex;
                if (instruction.OpCode.FlowControl == FlowControl.Branch) yield break;
            }
            else if (instruction.Operand is Instruction[] targets)
            {
                foreach (Instruction branchTarget in targets)
                    if (indexes.TryGetValue(branchTarget, out int targetIndex)) yield return targetIndex;
            }
            if (index + 1 < instructions.Count) yield return index + 1;
        }

        private static void ExecuteGenericStackBehaviour(OpCode opcode, State state)
        {
            int popCount = GetPopCount(opcode.StackBehaviourPop);
            var popped = new List<Value>();
            for (int index = 0; index < popCount; index++) popped.Add(Pop(state));
            int pushCount = GetPushCount(opcode.StackBehaviourPush);
            Value merged = Value.Union(popped);
            for (int index = 0; index < pushCount; index++) state.Stack.Add(merged.Clone());
        }

        private static int GetPopCount(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref: return 3;
                default: return 0;
            }
        }

        private static int GetPushCount(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref: return 1;
                case StackBehaviour.Push1_push1: return 2;
                default: return 0;
            }
        }

        private static Value Pop(State state)
        {
            if (state.Stack.Count == 0) return new Value();
            int index = state.Stack.Count - 1;
            Value value = state.Stack[index];
            state.Stack.RemoveAt(index);
            return value;
        }

        private static bool TryGetArgumentIndex(
            MethodDefinition method,
            Instruction instruction,
            out int parameterIndex)
        {
            parameterIndex = -1;
            int raw;
            switch (instruction.OpCode.Code)
            {
                case Code.Ldarg_0: raw = 0; break;
                case Code.Ldarg_1: raw = 1; break;
                case Code.Ldarg_2: raw = 2; break;
                case Code.Ldarg_3: raw = 3; break;
                case Code.Ldarg:
                case Code.Ldarg_S:
                    if (instruction.Operand is ParameterDefinition parameter)
                    {
                        parameterIndex = parameter.Index;
                        return true;
                    }
                    return false;
                default: return false;
            }
            parameterIndex = method.HasThis ? raw - 1 : raw;
            return true;
        }

        private static bool TryGetLocalIndex(Instruction instruction, bool load, out int index)
        {
            index = -1;
            Code code = instruction.OpCode.Code;
            if (load)
            {
                if (code == Code.Ldloc_0) { index = 0; return true; }
                if (code == Code.Ldloc_1) { index = 1; return true; }
                if (code == Code.Ldloc_2) { index = 2; return true; }
                if (code == Code.Ldloc_3) { index = 3; return true; }
                if (code != Code.Ldloc && code != Code.Ldloc_S) return false;
            }
            else
            {
                if (code == Code.Stloc_0) { index = 0; return true; }
                if (code == Code.Stloc_1) { index = 1; return true; }
                if (code == Code.Stloc_2) { index = 2; return true; }
                if (code == Code.Stloc_3) { index = 3; return true; }
                if (code != Code.Stloc && code != Code.Stloc_S) return false;
            }
            if (instruction.Operand is VariableDefinition variable)
            {
                index = variable.Index;
                return true;
            }
            return false;
        }

        private static bool TryGetUiRole(string type, string method, out string role)
        {
            role = string.Empty;
            bool widgets = type == "Verse.Widgets" || type == "Verse.Listing_Standard" ||
                           type == "UnityEngine.GUI";
            if (widgets && method.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                role = "button";
            else if (widgets && method.IndexOf("Checkbox", StringComparison.OrdinalIgnoreCase) >= 0)
                role = "settings_item";
            else if (widgets && method.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0)
                role = "label";
            else if (widgets && method.IndexOf("TextField", StringComparison.OrdinalIgnoreCase) >= 0)
                role = "text_field";
            else if (type == "Verse.TooltipHandler" && method.IndexOf("TipRegion", StringComparison.Ordinal) >= 0)
                role = "tooltip";
            else if (type == "Verse.Messages" && method == "Message")
                role = "message";
            else if (type == "Verse.FloatMenuOption" && method == ".ctor")
                role = "button";
            else if ((type == "Verse.Dialog_MessageBox" ||
                      type == "RimWorld.Dialog_MessageBox") && method == ".ctor")
                role = "message";
            else if ((type == "Verse.Command" || type.EndsWith("Command_Action", StringComparison.Ordinal)) &&
                     (method.StartsWith("set_", StringComparison.Ordinal) || method == ".ctor"))
                role = "label";
            return role.Length > 0;
        }

        private static bool TryGetNonUiReason(string type, string method, out string reason)
        {
            reason = string.Empty;
            if (type == "Verse.Log" || type == "UnityEngine.Debug" || type == "System.Console" ||
                type == "System.Diagnostics.Debug" || type == "System.Diagnostics.Trace")
                reason = "NON_UI_LOG";
            else if (type == "HarmonyLib.AccessTools" || type == "System.Type" ||
                     type.StartsWith("System.Reflection.", StringComparison.Ordinal))
                reason = "NON_UI_REFLECTION_KEY";
            else if (type.StartsWith("Verse.DefDatabase`1", StringComparison.Ordinal) &&
                     method.IndexOf("GetNamed", StringComparison.Ordinal) >= 0)
                reason = "NON_UI_DEF_NAME";
            else if (type == "Verse.DirectXmlCrossRefLoader" &&
                     method.IndexOf("RegisterObjectWantsCrossRef", StringComparison.Ordinal) >= 0)
                reason = "NON_UI_DEF_NAME";
            else if (type.StartsWith("Verse.ParseHelper", StringComparison.Ordinal) &&
                     method.IndexOf("FromString", StringComparison.Ordinal) >= 0)
                reason = "NON_UI_PARSE_KEY";
            else if (type == "Verse.Scribe_Values" || type == "Verse.Scribe_Defs" ||
                     type.StartsWith("Newtonsoft.Json", StringComparison.Ordinal))
                reason = "NON_UI_SERIALIZATION_KEY";
            else if (type == "System.IO.File" || type == "System.IO.Directory" ||
                     type == "System.Reflection.Assembly")
                reason = "NON_UI_FILE_PATH";
            return reason.Length > 0;
        }

        private static bool TryGetNonUiArgumentReason(
            MethodReference call,
            int parameterIndex,
            out string reason)
        {
            reason = string.Empty;
            if (call == null || parameterIndex < 0 || parameterIndex >= call.Parameters.Count)
                return false;
            string type = call.DeclaringType?.FullName ?? string.Empty;
            string method = call.Name ?? string.Empty;
            if (parameterIndex == 0 && IsTranslationLookup(type, method))
                reason = "NON_UI_TRANSLATION_KEY";
            else if (method == "ToString" && parameterIndex == 0 &&
                     call.ReturnType?.FullName == "System.String")
                reason = "NON_UI_FORMAT_STRING";
            else if ((type == "System.Enum" && (method == "Parse" || method == "TryParse")) ||
                     (parameterIndex == 0 && type == "System.Type" && method == "GetType"))
                reason = "NON_UI_TYPE_KEY";
            else if (parameterIndex == 0 &&
                     (type.StartsWith("System.Collections.Generic.Dictionary`2", StringComparison.Ordinal) ||
                      type.StartsWith("System.Collections.Generic.IDictionary`2", StringComparison.Ordinal)) &&
                     (method == "ContainsKey" || method == "TryGetValue" || method == "get_Item" ||
                      method == "set_Item" || method == "Add" || method == "Remove"))
                reason = "NON_UI_LOOKUP_KEY";
            else if (parameterIndex == 0 &&
                     type.StartsWith("Verse.ContentFinder`1", StringComparison.Ordinal) && method == "Get")
                reason = "NON_UI_ASSET_PATH";
            else if (type == "System.String" &&
                     (method == "op_Equality" || method == "op_Inequality" ||
                      method == "Equals" || method == "Compare" || method == "CompareOrdinal" ||
                      method == "StartsWith" || method == "EndsWith" || method == "Contains" ||
                      method == "IndexOf" || method == "LastIndexOf"))
                reason = "NON_UI_COMPARISON_KEY";
            else if (type.StartsWith("System.Collections.Generic.EqualityComparer`1", StringComparison.Ordinal) &&
                     method == "Equals")
                reason = "NON_UI_COMPARISON_KEY";
            else if (type == "System.Linq.Enumerable" && method == "Contains" && parameterIndex == 1)
                reason = "NON_UI_COMPARISON_KEY";
            return reason.Length > 0;
        }

        private static bool TryGetNonUiInstanceReason(MethodReference call, out string reason)
        {
            reason = string.Empty;
            if (call == null || !call.HasThis) return false;
            string type = call.DeclaringType?.FullName ?? string.Empty;
            string method = call.Name ?? string.Empty;
            if (type == "System.String" &&
                (method == "GetHashCode" || method == "Equals" || method == "StartsWith" ||
                 method == "EndsWith" || method == "Contains" || method == "IndexOf" ||
                 method == "LastIndexOf"))
                reason = "NON_UI_COMPARISON_KEY";
            return reason.Length > 0;
        }

        private static bool IsTranslationLookup(string type, string method)
        {
            if (method != "Translate") return false;
            return type == "Verse.Translator" ||
                   type.IndexOf("Translator", StringComparison.Ordinal) >= 0 ||
                   type.IndexOf("Translation", StringComparison.Ordinal) >= 0;
        }

        private static bool IsKnownStringPropagation(
            string type,
            string method,
            TypeReference returnType)
        {
            if (type == "System.String" && (method == "Concat" || method == "Format" || method == "op_Addition"))
                return true;
            if (type == "System.Text.StringBuilder" && (method == "Append" || method == "AppendFormat" || method == "ToString"))
                return true;
            if (method == "ToString" && returnType?.FullName == "System.String")
                return true;
            if ((method == "op_Implicit" || method == "op_Explicit") && IsStringLike(returnType))
                return true;
            return false;
        }

        private static bool IsEnumerablePropagation(
            string type,
            string method,
            TypeReference returnType)
        {
            if (type != "System.Linq.Enumerable") return false;
            string returnName = returnType?.FullName ?? string.Empty;
            bool returnsSequence = returnName.StartsWith(
                                       "System.Collections.Generic.IEnumerable`1",
                                       StringComparison.Ordinal) ||
                                   returnName.StartsWith(
                                       "System.Collections.Generic.List`1",
                                       StringComparison.Ordinal) ||
                                   returnName.EndsWith("[]", StringComparison.Ordinal);
            if (!returnsSequence) return false;
            return method == "AsEnumerable" || method == "Cast" || method == "OfType" ||
                   method == "Where" || method == "Select" || method == "SelectMany" ||
                   method == "Concat" || method == "Append" || method == "Prepend" ||
                   method == "Distinct" || method == "OrderBy" || method == "OrderByDescending" ||
                   method == "ThenBy" || method == "ThenByDescending" || method == "Reverse" ||
                   method == "Skip" || method == "Take" || method == "ToList" || method == "ToArray";
        }

        private static bool IsContainerType(string type)
        {
            return type.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal) ||
                   type.StartsWith("System.Collections.Generic.Dictionary`2", StringComparison.Ordinal) ||
                   type.StartsWith("System.Collections.Generic.HashSet`1", StringComparison.Ordinal) ||
                   type.StartsWith("System.Collections.Generic.Queue`1", StringComparison.Ordinal) ||
                   type.StartsWith("System.Collections.Generic.Stack`1", StringComparison.Ordinal);
        }

        private static bool IsCollectionMutation(string type, string method, out int valueIndex)
        {
            valueIndex = -1;
            if (!IsContainerType(type)) return false;
            if (type.StartsWith("System.Collections.Generic.Dictionary`2", StringComparison.Ordinal))
            {
                if (method == "Add" || method == "set_Item") valueIndex = 1;
                return valueIndex >= 0;
            }
            if (method == "Add" || method == "Enqueue" || method == "Push") valueIndex = 0;
            return valueIndex >= 0;
        }

        private static bool IsContainerRead(string type, string method)
        {
            bool collection = IsContainerType(type) ||
                              type == "System.Collections.IEnumerable" ||
                              type == "System.Collections.IEnumerator" ||
                              type.StartsWith("System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal) ||
                              type.StartsWith("System.Collections.Generic.IEnumerator`1", StringComparison.Ordinal) ||
                              type.IndexOf("/Enumerator", StringComparison.Ordinal) >= 0;
            if (!collection) return false;
            return method == "GetEnumerator" || method == "get_Current" || method == "get_Item" ||
                   method == "get_Values" || method == "get_Keys" || method == "Dequeue" ||
                   method == "Peek" || method == "Pop";
        }

        private static bool IsStoreElement(Code code)
        {
            return code == Code.Stelem_Any || code == Code.Stelem_I || code == Code.Stelem_I1 ||
                   code == Code.Stelem_I2 || code == Code.Stelem_I4 || code == Code.Stelem_I8 ||
                   code == Code.Stelem_R4 || code == Code.Stelem_R8 || code == Code.Stelem_Ref;
        }

        private static bool IsLoadElement(Code code)
        {
            return code == Code.Ldelem_Any || code == Code.Ldelem_I || code == Code.Ldelem_I1 ||
                   code == Code.Ldelem_I2 || code == Code.Ldelem_I4 || code == Code.Ldelem_I8 ||
                   code == Code.Ldelem_R4 || code == Code.Ldelem_R8 || code == Code.Ldelem_Ref ||
                   code == Code.Ldelem_U1 || code == Code.Ldelem_U2 || code == Code.Ldelem_U4;
        }

        private static void MergeIntoContainers(
            State state,
            AnalysisContext context,
            Value container,
            Value value)
        {
            foreach (string containerId in container?.Containers ?? Enumerable.Empty<string>())
            {
                if (!state.Heap.TryGetValue(containerId, out Value contents))
                {
                    contents = new Value();
                    state.Heap[containerId] = contents;
                }
                contents.Merge(value);
                context.MergeContainer(containerId, contents);
            }
        }

        private static Value ExpandContainers(State state, AnalysisContext context, Value value)
        {
            Value expanded = value?.Clone() ?? new Value();
            foreach (string containerId in expanded.Containers.ToList())
            {
                if (state.Heap.TryGetValue(containerId, out Value contents))
                    expanded.Merge(contents);
                else if (context.TryGetContainer(containerId, out Value sharedContents))
                {
                    state.Heap[containerId] = sharedContents.Clone();
                    expanded.Merge(sharedContents);
                }
            }
            return expanded;
        }

        private static TypeReference ResolveCallType(TypeReference type, MethodReference call)
        {
            if (type is ByReferenceType reference)
                return ResolveCallType(reference.ElementType, call);
            if (!(type is GenericParameter parameter)) return type;
            if (parameter.Type == GenericParameterType.Method && call is GenericInstanceMethod genericMethod &&
                parameter.Position >= 0 && parameter.Position < genericMethod.GenericArguments.Count)
                return genericMethod.GenericArguments[parameter.Position];
            if (parameter.Type == GenericParameterType.Type && call.DeclaringType is GenericInstanceType genericType &&
                parameter.Position >= 0 && parameter.Position < genericType.GenericArguments.Count)
                return genericType.GenericArguments[parameter.Position];
            return type;
        }

        private static bool IsStringLike(TypeReference type)
        {
            string name = type?.FullName ?? string.Empty;
            return name == "System.String" || name == "Verse.TaggedString" ||
                   name == "UnityEngine.GUIContent";
        }

        private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> roots)
        {
            foreach (TypeDefinition type in roots ?? Enumerable.Empty<TypeDefinition>())
            {
                yield return type;
                foreach (TypeDefinition nested in EnumerateTypes(type.NestedTypes)) yield return nested;
            }
        }

        private static DefaultAssemblyResolver CreateResolver(string assemblyPath)
        {
            var resolver = new DefaultAssemblyResolver();
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string ownDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (!string.IsNullOrWhiteSpace(ownDirectory)) directories.Add(ownDirectory);
            foreach (System.Reflection.Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string location = loaded.Location;
                    string directory = string.IsNullOrWhiteSpace(location)
                        ? string.Empty
                        : Path.GetDirectoryName(Path.GetFullPath(location));
                    if (!string.IsNullOrWhiteSpace(directory)) directories.Add(directory);
                }
                catch { }
            }
            foreach (string directory in directories) resolver.AddSearchDirectory(directory);
            return resolver;
        }

        private static void AddFlag(HardcodedUiDecisionRecord record, string flag)
        {
            if (record.DiagnosticFlags == null) record.DiagnosticFlags = new List<string>();
            if (!record.DiagnosticFlags.Contains(flag)) record.DiagnosticFlags.Add(flag);
        }

        private static string CreateLiteralId(int methodMetadataToken, int literalOrdinal)
        {
            return methodMetadataToken.ToString("x8", CultureInfo.InvariantCulture) + ":" +
                   literalOrdinal.ToString(CultureInfo.InvariantCulture);
        }

        private static string CreateAllocationId(MethodDefinition method, Instruction instruction)
        {
            int token = method?.MetadataToken.ToInt32() ?? 0;
            return token.ToString("x8", CultureInfo.InvariantCulture) + "@" +
                   (instruction?.Offset ?? -1).ToString("x4", CultureInfo.InvariantCulture);
        }

        private static void MergeLiteralFact(
            IDictionary<string, LiteralFact> target,
            string literalId,
            LiteralFact source)
        {
            if (source == null) return;
            if (!target.TryGetValue(literalId, out LiteralFact current))
            {
                current = new LiteralFact();
                target[literalId] = current;
            }
            current.UiRoles.UnionWith(source.UiRoles);
            current.NonUiReasons.UnionWith(source.NonUiReasons);
            foreach (string evidence in source.Evidence)
                if (current.Evidence.Count < 16 && !current.Evidence.Contains(evidence))
                    current.Evidence.Add(evidence);
        }
    }
}
