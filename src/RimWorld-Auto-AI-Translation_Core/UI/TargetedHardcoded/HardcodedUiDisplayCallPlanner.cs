using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    // Track exact literal provenance within straight-line IL. No source value is mutated.
    // Branch joins, address-taking and unknown operations discard provenance conservatively.
    internal static class HardcodedUiDisplayCallPlanner
    {
        internal sealed class Site
        {
            public int InstructionIndex;
            public MethodBase Call;
            public Dictionary<int, HardcodedUiTranspileSpec> Arguments = new Dictionary<int, HardcodedUiTranspileSpec>();
        }

        internal static List<Site> Plan(List<CodeInstruction> body, IList<HardcodedUiTranspileSpec> specs)
        {
            var byOrdinal = specs.ToDictionary(s => s.LiteralOrdinal);
            var stack = new List<HardcodedUiTranspileSpec>();
            var locals = new Dictionary<int, HardcodedUiTranspileSpec>();
            var sites = new List<Site>();
            int ordinal = -1;
            for (int i = 0; i < body.Count; i++)
            {
                CodeInstruction instruction = body[i];
                OpCode op = instruction.opcode;
                if (instruction.labels.Count > 0 || instruction.blocks.Count > 0)
                { stack.Clear(); locals.Clear(); }
                if (op == OpCodes.Ldstr)
                {
                    ordinal++;
                    byOrdinal.TryGetValue(ordinal, out HardcodedUiTranspileSpec spec);
                    stack.Add(spec != null && string.Equals(spec.Literal, instruction.operand as string, StringComparison.Ordinal) ? spec : null);
                    continue;
                }
                int local = LocalIndex(instruction);
                if (local >= 0 && op.Name.StartsWith("stloc", StringComparison.Ordinal))
                { locals[local] = Pop(stack); continue; }
                if (local >= 0 && op.Name.StartsWith("ldloc", StringComparison.Ordinal) && !op.Name.StartsWith("ldloca", StringComparison.Ordinal))
                { locals.TryGetValue(local, out HardcodedUiTranspileSpec value); stack.Add(value); continue; }
                if (op == OpCodes.Dup)
                { stack.Add(stack.Count > 0 ? stack[stack.Count - 1] : null); continue; }
                if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                {
                    MethodBase call = instruction.operand as MethodBase;
                    if (call == null) { stack.Clear(); locals.Clear(); continue; }
                    ParameterInfo[] parameters = call.GetParameters();
                    var arguments = new HardcodedUiTranspileSpec[parameters.Length];
                    for (int p = parameters.Length - 1; p >= 0; p--) arguments[p] = Pop(stack);
                    if (!call.IsStatic && op != OpCodes.Newobj) Pop(stack);
                    if (op != OpCodes.Newobj && HardcodedUiCallTarget.IsDisplayCall(call) &&
                        instruction.labels.Count == 0 && instruction.blocks.Count == 0 &&
                        (i == 0 || body[i - 1].opcode.OpCodeType != OpCodeType.Prefix) &&
                        parameters.All(p => !p.ParameterType.IsByRef && !p.ParameterType.IsPointer))
                    {
                        var site = new Site { InstructionIndex = i, Call = call };
                        for (int p = 0; p < parameters.Length; p++)
                            if (parameters[p].ParameterType == typeof(string) && arguments[p] != null)
                                site.Arguments[p] = arguments[p];
                        if (site.Arguments.Count > 0) sites.Add(site);
                    }
                    if (op == OpCodes.Newobj || call is MethodInfo info && info.ReturnType != typeof(void)) stack.Add(null);
                    // A ref argument may overwrite a tracked local.
                    if (parameters.Any(p => p.ParameterType.IsByRef)) locals.Clear();
                    continue;
                }
                if (op == OpCodes.Nop) continue;
                if (op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Cond_Branch ||
                    op.FlowControl == FlowControl.Return || op.FlowControl == FlowControl.Throw ||
                    op.Name.StartsWith("ldloca", StringComparison.Ordinal))
                { stack.Clear(); locals.Clear(); continue; }
                int pops = StackCount(op.StackBehaviourPop);
                int pushes = StackCount(op.StackBehaviourPush);
                if (pops < 0 || pushes < 0) { stack.Clear(); locals.Clear(); continue; }
                for (int p = 0; p < pops; p++) Pop(stack);
                for (int p = 0; p < pushes; p++) stack.Add(null);
            }
            return sites;
        }

        private static HardcodedUiTranspileSpec Pop(List<HardcodedUiTranspileSpec> stack)
        {
            if (stack.Count == 0) return null;
            var value = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); return value;
        }

        private static int LocalIndex(CodeInstruction instruction)
        {
            string name = instruction.opcode.Name;
            if (!name.StartsWith("ldloc", StringComparison.Ordinal) && !name.StartsWith("stloc", StringComparison.Ordinal)) return -1;
            if (name.Length == 7 && name[6] >= '0' && name[6] <= '3') return name[6] - '0';
            if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
            if (instruction.operand is LocalVariableInfo variable) return variable.LocalIndex;
            if (instruction.operand is byte b) return b;
            if (instruction.operand is short s) return s;
            if (instruction.operand is int n) return n;
            return -1;
        }

        private static int StackCount(StackBehaviour behaviour)
        {
            if (behaviour == StackBehaviour.Pop0 || behaviour == StackBehaviour.Push0) return 0;
            if (behaviour == StackBehaviour.Varpop || behaviour == StackBehaviour.Varpush) return -1;
            // Fixed behaviours encode one component per stack value (e.g. Popref_popi_pop1).
            return behaviour.ToString().Split('_').Length;
        }
    }
}
