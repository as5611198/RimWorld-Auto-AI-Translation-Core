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
            MethodBase original)
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

            Dictionary<int, HardcodedUiTranspileSpec> byOrdinal = specs
                .GroupBy(spec => spec.LiteralOrdinal)
                .ToDictionary(group => group.Key, group => group.First());
            List<CodeInstruction> output = new List<CodeInstruction>(source.Count + specs.Count * 2);
            int literalOrdinal = -1;
            int replacementCount = 0;

            for (int index = 0; index < source.Count; index++)
            {
                CodeInstruction instruction = source[index];
                bool isLiteral = instruction.opcode == OpCodes.Ldstr && instruction.operand is string;
                if (isLiteral) literalOrdinal++;
                output.Add(instruction);

                if (!isLiteral || !byOrdinal.ContainsKey(literalOrdinal)) continue;
                HardcodedUiTranspileSpec spec = byOrdinal[literalOrdinal];
                if (!string.Equals((string)instruction.operand, spec.Literal, StringComparison.Ordinal)) continue;

                if (!string.IsNullOrWhiteSpace(spec.CallSignature))
                {
                    int next = index + 1;
                    while (next < source.Count && source[next].opcode == OpCodes.Nop) next++;
                    MethodBase call = next < source.Count ? source[next].operand as MethodBase : null;
                    if (next >= source.Count || !HardcodedUiCallTarget.IsSupported(call) ||
                        !string.Equals(HardcodedUiMethodIdentity.GetMethodSignature(call), spec.CallSignature, StringComparison.Ordinal)) continue;
                }

                output.Add(new CodeInstruction(OpCodes.Ldstr, spec.EntryId));
                output.Add(new CodeInstruction(OpCodes.Call, ResolveMethod));
                replacementCount++;
            }

            if (replacementCount != byOrdinal.Count)
            {
                throw new InvalidOperationException(
                    "Hardcoded UI transpiler matched " + replacementCount +
                    " of " + byOrdinal.Count + " approved literals.");
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
