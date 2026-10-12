using HarmonyLib;
using Mono.Cecil;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Verse;
using AutoTranslator_Core.Workflow;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal sealed class HardcodedUiScanResult
    {
        internal readonly List<HardcodedUiPatchEntry> Entries = new List<HardcodedUiPatchEntry>();
        internal readonly List<string> Diagnostics = new List<string>();
        internal readonly Dictionary<string, HardcodedUiDecisionRecord> Decisions =
            new Dictionary<string, HardcodedUiDecisionRecord>(StringComparer.Ordinal);
        internal int AssemblyCount;
        internal int MethodCount;
    }

    internal static class HardcodedUiRuntimeScanner
    {
        private static readonly SemaphoreSlim RuntimeReflectionGate = new SemaphoreSlim(1, 1);

        private sealed class CecilWorkItem
        {
            internal int Index;
            internal string AssemblyRelativePath;
            internal string AssemblyPath;
            internal List<HardcodedUiPatchEntry> Entries;
        }

        private sealed class RuntimeAssemblySource
        {
            internal Assembly RuntimeAssembly;
            internal string SourcePath;
        }

        internal static HardcodedUiScanResult Scan(ModMetaData mod)
        {
            return Scan(mod, true);
        }

        internal static HardcodedUiScanResult Scan(ModMetaData mod, bool persistLegacyDecisionState)
        {
            return Scan(mod, persistLegacyDecisionState, null, CancellationToken.None);
        }

        internal static HardcodedUiScanResult Scan(
            ModMetaData mod,
            bool persistLegacyDecisionState,
            Action<double, string> reportProgress,
            CancellationToken cancellationToken)
        {
            var result = new HardcodedUiScanResult();
            if (mod == null || mod.RootDir == null || string.IsNullOrWhiteSpace(mod.PackageId))
            {
                result.Diagnostics.Add("Invalid Mod metadata.");
                return result;
            }

            string root = Path.GetFullPath(mod.RootDir.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootPrefix = root + Path.DirectorySeparatorChar;
            List<RuntimeAssemblySource> assemblies;
            reportProgress?.Invoke(
                0d,
                AutoTranslatorMod.WfText(
                    "阶段 1/2 · 准备运行时扫描",
                    "stage 1/2 · preparing runtime scan"));
            AutoTranslatorSettings.AddLog(
                "• DLL " + AutoTranslatorMod.WfText("分析", "analysis") + " · " +
                (mod.Name ?? mod.PackageId) +
                AutoTranslatorMod.WfText(
                    " · 等待运行时扫描队列",
                    " · waiting for runtime-scan queue"));
            RuntimeReflectionGate.Wait(cancellationToken);
            try
            {
                AutoTranslatorSettings.AddLog(
                    "▶ DLL " + AutoTranslatorMod.WfText("分析", "analysis") + " · " +
                    (mod.Name ?? mod.PackageId) +
                    AutoTranslatorMod.WfText(
                        " · 阶段 1/2：运行时扫描",
                        " · stage 1/2: runtime scan"));
                IReadOnlyList<string> effectiveAssemblyPaths = AutoTranslatorScanner.GetAllEffectiveAssemblyPaths(mod);
                assemblies = ResolveRuntimeAssemblySources(
                    rootPrefix,
                    effectiveAssemblyPaths,
                    result.Diagnostics,
                    cancellationToken);
                result.AssemblyCount = assemblies.Count;
                int runtimeAssemblyIndex = 0;

                foreach (RuntimeAssemblySource source in assemblies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assembly assembly = source.RuntimeAssembly;
                    string location = source.SourcePath;
                    string relativePath = location.Substring(rootPrefix.Length)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    string assemblyHash = HardcodedUiMethodIdentity.ComputeFileSha256(location);
                    string mvid = assembly.ManifestModule.ModuleVersionId.ToString("D");
                    List<Type> loadableTypes = GetLoadableTypes(assembly).ToList();
                    int typeProgressInterval = Math.Max(1, loadableTypes.Count / 100);
                    AutoTranslatorSettings.AddDebugLog(
                        "dll.analysis runtime-start mod=" + mod.PackageId +
                        " assembly=" + relativePath +
                        " types=" + loadableTypes.Count);

                    for (int typeIndex = 0; typeIndex < loadableTypes.Count; typeIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Type type = loadableTypes[typeIndex];
                        List<MethodBase> methods;
                        try
                        {
                            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                            methods = type.GetMethods(declared)
                                .Cast<MethodBase>()
                                .Concat(type.GetConstructors(declared).Cast<MethodBase>())
                                .GroupBy(method =>
                                {
                                    try { return method.Module.ModuleVersionId + ":" + method.MetadataToken; }
                                    catch { return HardcodedUiMethodIdentity.GetMethodSignature(method); }
                                }, StringComparer.Ordinal)
                                .Select(group => group.First())
                                .ToList();
                        }
                        catch (Exception ex)
                        {
                            result.Diagnostics.Add(type.FullName + ": " + ex.Message);
                            methods = new List<MethodBase>();
                        }

                        foreach (MethodBase method in methods)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (method.IsAbstract || method.ContainsGenericParameters) continue;
                            ScanMethod(
                                method,
                                mod.PackageId,
                                relativePath,
                                assemblyHash,
                                mvid,
                                result,
                                cancellationToken);
                        }
                        int completedTypes = typeIndex + 1;
                        if (completedTypes == loadableTypes.Count ||
                            completedTypes % typeProgressInterval == 0)
                        {
                            double assemblyFraction = loadableTypes.Count == 0
                                ? 1d
                                : (double)completedTypes / loadableTypes.Count;
                            reportProgress?.Invoke(
                                assemblies.Count == 0
                                    ? 0.5d
                                    : 0.5d * (runtimeAssemblyIndex + assemblyFraction) / assemblies.Count,
                                AutoTranslatorMod.WfText("运行时扫描 ", "runtime scan ") + relativePath +
                                AutoTranslatorMod.WfText(" · 阶段 1/2", " · stage 1/2") +
                                AutoTranslatorMod.WfText(" · 类型 ", " · type ") +
                                completedTypes + "/" + loadableTypes.Count +
                                AutoTranslatorMod.WfText(" · 程序集 ", " · assembly ") +
                                (runtimeAssemblyIndex + 1) + "/" + assemblies.Count);
                        }
                    }
                    runtimeAssemblyIndex++;
                    AutoTranslatorSettings.AddDebugLog(
                        "dll.analysis runtime-complete mod=" + mod.PackageId +
                        " assembly=" + relativePath +
                        " scannedMethods=" + result.MethodCount);
                    reportProgress?.Invoke(
                        assemblies.Count == 0
                            ? 0.5d
                            : 0.5d * runtimeAssemblyIndex / assemblies.Count,
                        AutoTranslatorMod.WfText("运行时扫描 ", "runtime scan ") + relativePath +
                        AutoTranslatorMod.WfText(" · 阶段 1/2", " · stage 1/2") +
                        AutoTranslatorMod.WfText(" · 程序集 ", " · assembly ") +
                        runtimeAssemblyIndex + "/" + assemblies.Count);
                }
            }
            finally
            {
                RuntimeReflectionGate.Release();
            }

            cancellationToken.ThrowIfCancellationRequested();
            result.Entries.Sort((left, right) => string.Compare(left.EntryId, right.EntryId, StringComparison.Ordinal));
            Dictionary<string, HardcodedUiDecisionRecord> baselineDecisions;
            try
            {
                if (persistLegacyDecisionState)
                    baselineDecisions = HardcodedUiDecisionState.Analyze(result.Entries);
                else
                {
                    baselineDecisions = new Dictionary<string, HardcodedUiDecisionRecord>(StringComparer.Ordinal);
                    foreach (HardcodedUiPatchEntry entry in result.Entries)
                        baselineDecisions[entry.EntryId] = HardcodedUiBaselineDecisionAnalyzer.Analyze(entry);
                }
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add("Decision store unavailable: " + ex.Message);
                baselineDecisions = result.Entries.ToDictionary(
                    entry => entry.EntryId,
                    entry => HardcodedUiBaselineDecisionAnalyzer.Analyze(entry),
                    StringComparer.Ordinal);
            }

            foreach (KeyValuePair<string, HardcodedUiDecisionRecord> pair in baselineDecisions)
                result.Decisions[pair.Key] = pair.Value;

            List<CecilWorkItem> workItems = result.Entries
                .GroupBy(entry => entry.AssemblyRelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select((group, index) =>
                {
                    string assemblyRelativePath = group.Key ?? string.Empty;
                    string assemblyPath = Path.Combine(
                        root,
                        assemblyRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(assemblyPath))
                        throw new FileNotFoundException(
                            "DLL static-analysis input disappeared before scheduling.",
                            assemblyPath);
                    List<HardcodedUiPatchEntry> entries = group
                        .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
                        .ToList();
                    return new CecilWorkItem
                    {
                        Index = index,
                        AssemblyRelativePath = assemblyRelativePath,
                        AssemblyPath = assemblyPath,
                        Entries = entries
                    };
                })
                .ToList();

            AutoTranslatorSettings.AddLog(
                "▶ DLL " + AutoTranslatorMod.WfText("分析", "analysis") + " · " +
                (mod.Name ?? mod.PackageId) +
                AutoTranslatorMod.WfText(
                    " · 阶段 2/2：静态分析 · 程序集 ",
                    " · stage 2/2: static analysis · assemblies ") +
                workItems.Count);
            if (workItems.Count > 0)
            {
                AutoTranslatorSettings.AddDebugLog(
                    "dll.analysis cecil-serial mod=" + mod.PackageId +
                    " assemblies=" + workItems.Count);
                foreach (CecilWorkItem item in workItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AutoTranslatorSettings.AddDebugLog(
                        "dll.analysis cecil-start mod=" + mod.PackageId +
                        " assembly=" + item.AssemblyRelativePath +
                        " mode=serial");
                    HardcodedUiIlAnalysisResult analysis = HardcodedUiIlDataflowAnalyzer.Analyze(
                        item.AssemblyPath,
                        item.Entries,
                        baselineDecisions,
                        null,
                        analysisProgress =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            double assemblyRatio = Math.Max(
                                0d, Math.Min(1d, analysisProgress.OverallRatio));
                            double overallRatio = 0.5d + (0.5d *
                                (item.Index + assemblyRatio) / workItems.Count);
                            string detail =
                                AutoTranslatorMod.WfText("静态分析 ", "static analysis ") +
                                item.AssemblyRelativePath + " · " + analysisProgress.Phase +
                                AutoTranslatorMod.WfText(" · 阶段 2/2", " · stage 2/2") +
                                (analysisProgress.Total > 0
                                    ? " " + analysisProgress.Completed + "/" + analysisProgress.Total
                                    : string.Empty) +
                                AutoTranslatorMod.WfText(" · 子阶段 ", " · substage ") +
                                analysisProgress.StageIndex + "/" + analysisProgress.StageCount +
                                AutoTranslatorMod.WfText(" · 程序集 ", " · assembly ") +
                                (item.Index + 1) + "/" + workItems.Count;
                            reportProgress?.Invoke(overallRatio, detail);
                        },
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (KeyValuePair<string, HardcodedUiDecisionRecord> pair in
                             analysis.Decisions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        result.Decisions[pair.Key] = pair.Value;
                    result.Diagnostics.AddRange(analysis.Diagnostics.Select(diagnostic =>
                        "Cecil " + item.AssemblyRelativePath + ": " + diagnostic));
                    AutoTranslatorSettings.AddDebugLog(
                        "dll.analysis cecil-complete mod=" + mod.PackageId +
                        " assembly=" + item.AssemblyRelativePath +
                        " diagnostics=" + analysis.Diagnostics.Count);
                    reportProgress?.Invoke(
                        0.5d + (0.5d * (item.Index + 1d) / workItems.Count),
                        AutoTranslatorMod.WfText("静态分析完成 ", "static analysis complete ") +
                        item.AssemblyRelativePath +
                        AutoTranslatorMod.WfText(" · 阶段 2/2 · 程序集 ", " · stage 2/2 · assembly ") +
                        (item.Index + 1) + "/" + workItems.Count);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (persistLegacyDecisionState)
            {
                try
                {
                    HardcodedUiDecisionState.Persist(result.Decisions.Values);
                }
                catch (Exception ex)
                {
                    result.Diagnostics.Add("Decision store unavailable: " + ex.Message);
                }
            }

            reportProgress?.Invoke(1d, AutoTranslatorMod.WfText("完成", "complete"));
            return result;
        }

        private static void ScanMethod(
            MethodBase method,
            string packageId,
            string relativePath,
            string assemblyHash,
            string mvid,
            HardcodedUiScanResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string signature = HardcodedUiMethodIdentity.GetMethodSignature(method);
            string fingerprint = HardcodedUiMethodIdentity.ComputeMethodIlFingerprint(method);
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                result.Diagnostics.Add("IL fingerprint failed: " + signature);
                return;
            }

            List<KeyValuePair<OpCode, object>> instructions;
            try
            {
                if (method.GetMethodBody() == null) return;
                instructions = PatchProcessor.ReadMethodBody(method).ToList();
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add("IL read failed: " +
                    (method.DeclaringType?.FullName ?? "<unknown>") + "." + method.Name + ": " + ex.Message);
                return;
            }
            result.MethodCount++;

            bool methodHasPlayerFacingSink = false;
            for (int instructionIndex = 0; instructionIndex < instructions.Count; instructionIndex++)
            {
                if ((instructionIndex & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                KeyValuePair<OpCode, object> item = instructions[instructionIndex];
                if ((item.Key == OpCodes.Call || item.Key == OpCodes.Callvirt || item.Key == OpCodes.Newobj) &&
                    HardcodedUiCallTarget.IsPlayerFacingSink(item.Value as MethodBase))
                {
                    methodHasPlayerFacingSink = true;
                    break;
                }
            }

            int literalOrdinal = -1;
            for (int index = 0; index < instructions.Count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                KeyValuePair<OpCode, object> instruction = instructions[index];
                if (instruction.Key != OpCodes.Ldstr || !(instruction.Value is string)) continue;
                literalOrdinal++;
                string literal = (string)instruction.Value;
                if (!IsCandidateText(literal)) continue;

                MethodBase call = FindImmediateSupportedCall(instructions, index);
                string discoveryKind = call != null
                    ? "direct_ui_call"
                    : methodHasPlayerFacingSink
                        ? "ui_method_literal"
                        : "review_string_literal";

                result.Entries.Add(new HardcodedUiPatchEntry
                {
                    EntryId = HardcodedUiMethodIdentity.CreateEntryId(
                        packageId, relativePath, signature, literalOrdinal, literal),
                    Enabled = false,
                    PackageId = packageId,
                    AssemblyRelativePath = HardcodedUiMethodIdentity.NormalizeRelativePath(relativePath),
                    AssemblySha256 = assemblyHash,
                    AssemblyMvid = mvid,
                    DeclaringType = method.DeclaringType.FullName,
                    MethodName = method.Name,
                    MethodSignature = signature,
                    MethodMetadataToken = method.MetadataToken,
                    MethodIlFingerprint = fingerprint,
                    Literal = literal,
                    LiteralOrdinal = literalOrdinal,
                    CallDeclaringType = call?.DeclaringType?.FullName ?? string.Empty,
                    CallMethodName = call?.Name ?? string.Empty,
                    CallSignature = call != null ? HardcodedUiMethodIdentity.GetMethodSignature(call) : string.Empty,
                    DiscoveryKind = discoveryKind
                });
            }
        }

        private static MethodBase FindImmediateSupportedCall(
            IList<KeyValuePair<OpCode, object>> instructions,
            int literalIndex)
        {
            int next = literalIndex + 1;
            while (next < instructions.Count && instructions[next].Key == OpCodes.Nop) next++;
            if (next >= instructions.Count ||
                (instructions[next].Key != OpCodes.Call && instructions[next].Key != OpCodes.Callvirt)) return null;
            MethodBase call = instructions[next].Value as MethodBase;
            return HardcodedUiCallTarget.IsSupported(call) ? call : null;
        }

        private static bool IsCandidateText(string value)
        {
            // Discovery is deliberately recall-first. Whether a literal is actually
            // player-facing belongs to the Agent prediction / human review stage. Reject
            // only values that cannot reasonably be natural-language text; identifiers,
            // file names and one-word labels must remain visible to later stages.
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return false;
            if (value.IndexOf('\0') >= 0 || value.Trim().Length < 2) return false;
            string trimmed = value.Trim();
            if (!trimmed.Any(char.IsLetter)) return false;
            return true;
        }

        private static bool IsAssemblyInsideRoot(Assembly assembly, string rootPrefix)
        {
            string location = SafeLocation(assembly);
            return location.Length > rootPrefix.Length &&
                   location.StartsWith(rootPrefix, WorkflowPath.Comparison) &&
                   File.Exists(location);
        }

        private static List<RuntimeAssemblySource> ResolveRuntimeAssemblySources(
            string rootPrefix,
            IEnumerable<string> effectiveAssemblyPaths,
            ICollection<string> diagnostics,
            CancellationToken cancellationToken)
        {
            List<Assembly> loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            List<string> normalizedEffectivePaths = (effectiveAssemblyPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => Path.GetFullPath(path))
                .Distinct(WorkflowPath.Comparer)
                .OrderBy(path => path, WorkflowPath.Comparer)
                .ToList();
            var effectivePathSet = new HashSet<string>(normalizedEffectivePaths, WorkflowPath.Comparer);
            var result = new List<RuntimeAssemblySource>();
            var addedRuntimeAssemblies = new HashSet<Assembly>();
            var loadedByMvid = new Dictionary<Guid, Assembly>();

            foreach (Assembly assembly in loadedAssemblies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Guid mvid = assembly.ManifestModule.ModuleVersionId;
                    if (!loadedByMvid.ContainsKey(mvid)) loadedByMvid[mvid] = assembly;
                }
                catch { }

                string directLocation = SafeLocation(assembly);
                if (!IsAssemblyInsideRoot(assembly, rootPrefix) ||
                    !effectivePathSet.Contains(directLocation) ||
                    !addedRuntimeAssemblies.Add(assembly))
                    continue;
                result.Add(new RuntimeAssemblySource
                {
                    RuntimeAssembly = assembly,
                    SourcePath = directLocation
                });
            }

            foreach (string diskPath in normalizedEffectivePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using (ModuleDefinition module = ModuleDefinition.ReadModule(
                               diskPath,
                               new ReaderParameters { ReadingMode = ReadingMode.Deferred, ReadSymbols = false }))
                    {
                        if (!loadedByMvid.TryGetValue(module.Mvid, out Assembly loaded) ||
                            !addedRuntimeAssemblies.Add(loaded))
                            continue;
                        result.Add(new RuntimeAssemblySource
                        {
                            RuntimeAssembly = loaded,
                            SourcePath = Path.GetFullPath(diskPath)
                        });
                        diagnostics?.Add(
                            "Resolved loaded assembly by MVID because its runtime location was outside the Mod root: " +
                            HardcodedUiMethodIdentity.NormalizeRelativePath(
                                Path.GetFullPath(diskPath).Substring(rootPrefix.Length)));
                    }
                }
                catch (BadImageFormatException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return result.OrderBy(item => item.SourcePath, WorkflowPath.Comparer).ToList();
        }

        private static string SafeLocation(Assembly assembly)
        {
            try { return Path.GetFullPath(assembly.Location ?? string.Empty); }
            catch { return string.Empty; }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

    }
}
