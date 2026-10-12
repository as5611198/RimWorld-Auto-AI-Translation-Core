using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal sealed class HardcodedUiTranspileSpec
    {
        public string EntryId;
        public string Literal;
        public int LiteralOrdinal;
        public string CallSignature;
    }

    internal static class HardcodedUiTargetedPatchTranspiler
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, List<HardcodedUiTranspileSpec>> SpecsByRuntimeMethod =
            new Dictionary<string, List<HardcodedUiTranspileSpec>>(StringComparer.Ordinal);
        private static readonly MethodInfo ResolveMethod =
            AccessTools.Method(typeof(HardcodedUiRuntime), nameof(HardcodedUiRuntime.Resolve));

        public static void SetSpecs(MethodBase method, IEnumerable<HardcodedUiTranspileSpec> specs)
        {
            string methodIdentity = HardcodedUiMethodIdentity.GetRuntimeMethodIdentity(method);
            if (string.IsNullOrWhiteSpace(methodIdentity)) return;
            lock (Gate)
            {
                SpecsByRuntimeMethod[methodIdentity] = CloneSpecs(specs);
            }
        }

        public static void RemoveSpecs(MethodBase method)
        {
            string methodIdentity = HardcodedUiMethodIdentity.GetRuntimeMethodIdentity(method);
            if (string.IsNullOrWhiteSpace(methodIdentity)) return;
            lock (Gate) SpecsByRuntimeMethod.Remove(methodIdentity);
        }

        public static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions,
            MethodBase original,
            ILGenerator generator = null)
        {
            List<CodeInstruction> source = instructions != null
                ? instructions.ToList()
                : new List<CodeInstruction>();
            if (original == null || source.Count == 0) return source;

            List<HardcodedUiTranspileSpec> specs;
            lock (Gate)
            {
                if (!SpecsByRuntimeMethod.TryGetValue(
                    HardcodedUiMethodIdentity.GetRuntimeMethodIdentity(original), out specs))
                    return source;
                specs = specs.ToList();
            }

            var sites = HardcodedUiDisplayCallPlanner.Plan(source, specs);
            var matched = new HashSet<string>(sites.SelectMany(s => s.Arguments.Values).Select(s => s.EntryId));
            if (specs.Any(s => !matched.Contains(s.EntryId)))
                throw new InvalidOperationException("DLL text has no unambiguous supported display argument.");
            var byIndex = sites.ToDictionary(s => s.InstructionIndex);
            if (generator == null) PatchProcessor.GetOriginalInstructions(original, out generator);
            var output = new List<CodeInstruction>();
            for (int index = 0; index < source.Count; index++)
            {
                if (byIndex.TryGetValue(index, out HardcodedUiDisplayCallPlanner.Site site))
                {
                    ParameterInfo[] parameters = site.Call.GetParameters();
                    LocalBuilder[] temporary = parameters.Select(p => generator.DeclareLocal(p.ParameterType)).ToArray();
                    // Preserve the receiver below the arguments, as well as all non-display values.
                    for (int p = parameters.Length - 1; p >= 0; p--)
                        output.Add(new CodeInstruction(OpCodes.Stloc, temporary[p]));
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        output.Add(new CodeInstruction(OpCodes.Ldloc, temporary[p]));
                        if (site.Arguments.TryGetValue(p, out HardcodedUiTranspileSpec spec))
                        {
                            output.Add(new CodeInstruction(OpCodes.Ldstr, spec.EntryId));
                            output.Add(new CodeInstruction(OpCodes.Call, ResolveMethod));
                        }
                    }
                }
                output.Add(source[index]);
            }
            return output;
        }

        private static List<HardcodedUiTranspileSpec> CloneSpecs(IEnumerable<HardcodedUiTranspileSpec> specs)
        {
            return (specs ?? Enumerable.Empty<HardcodedUiTranspileSpec>())
                .Where(spec => spec != null && !string.IsNullOrWhiteSpace(spec.EntryId))
                .Select(spec => new HardcodedUiTranspileSpec
                {
                    EntryId = spec.EntryId,
                    Literal = spec.Literal ?? string.Empty,
                    LiteralOrdinal = spec.LiteralOrdinal,
                    CallSignature = spec.CallSignature ?? string.Empty
                })
                .ToList();
        }
    }
}
