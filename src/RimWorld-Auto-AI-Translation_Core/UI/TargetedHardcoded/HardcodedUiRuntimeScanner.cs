using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;

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
        internal static HardcodedUiScanResult Scan(ModMetaData mod)
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
            List<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => IsAssemblyInsideRoot(assembly, rootPrefix))
                .OrderBy(assembly => SafeLocation(assembly), StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.AssemblyCount = assemblies.Count;

            foreach (Assembly assembly in assemblies)
            {
                string location = SafeLocation(assembly);
                string relativePath = location.Substring(rootPrefix.Length)
                    .Replace(Path.DirectorySeparatorChar, '/');
                string assemblyHash = HardcodedUiMethodIdentity.ComputeFileSha256(location);
                string mvid = assembly.ManifestModule.ModuleVersionId.ToString("D");

                foreach (Type type in GetLoadableTypes(assembly))
                {
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
                        continue;
                    }

                    foreach (MethodBase method in methods)
                    {
                        if (method.IsAbstract || method.ContainsGenericParameters) continue;
                        ScanMethod(method, mod.PackageId, relativePath, assemblyHash, mvid, result);
                    }
                }
            }

            result.Entries.Sort((left, right) => string.Compare(left.EntryId, right.EntryId, StringComparison.Ordinal));
            try
            {
                foreach (KeyValuePair<string, HardcodedUiDecisionRecord> pair in
                         HardcodedUiDecisionState.AnalyzeAndPersist(result.Entries))
                    result.Decisions[pair.Key] = pair.Value;

                foreach (IGrouping<string, HardcodedUiPatchEntry> assemblyEntries in result.Entries
                             .GroupBy(entry => entry.AssemblyRelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    string assemblyPath = Path.Combine(
                        root,
                        (assemblyEntries.Key ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
                    HardcodedUiIlAnalysisResult analysis = HardcodedUiIlDataflowAnalyzer.Analyze(
                        assemblyPath,
                        assemblyEntries,
                        result.Decisions);
                    foreach (KeyValuePair<string, HardcodedUiDecisionRecord> pair in analysis.Decisions)
                        result.Decisions[pair.Key] = pair.Value;
                    result.Diagnostics.AddRange(analysis.Diagnostics.Select(diagnostic =>
                        "Cecil " + assemblyEntries.Key + ": " + diagnostic));
                }
                HardcodedUiDecisionState.Persist(result.Decisions.Values);
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add("Decision store unavailable: " + ex.Message);
                foreach (HardcodedUiPatchEntry entry in result.Entries)
                    result.Decisions[entry.EntryId] = HardcodedUiBaselineDecisionAnalyzer.Analyze(entry);
            }
            return result;
        }

        private static void ScanMethod(
            MethodBase method,
            string packageId,
            string relativePath,
            string assemblyHash,
            string mvid,
            HardcodedUiScanResult result)
        {
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

            bool methodHasPlayerFacingSink = instructions.Any(item =>
                (item.Key == OpCodes.Call || item.Key == OpCodes.Callvirt || item.Key == OpCodes.Newobj) &&
                HardcodedUiCallTarget.IsPlayerFacingSink(item.Value as MethodBase));

            int literalOrdinal = -1;
            for (int index = 0; index < instructions.Count; index++)
            {
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
                   location.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(location);
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
