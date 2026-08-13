using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal sealed class HardcodedUiIlAnalysisResult
    {
        internal readonly Dictionary<string, HardcodedUiDecisionRecord> Decisions =
            new Dictionary<string, HardcodedUiDecisionRecord>(StringComparer.Ordinal);
        internal readonly List<string> Diagnostics = new List<string>();
    }

    internal static class HardcodedUiIlDataflowAnalyzer
    {
        internal const int AnalyzerVersion = 2;
        private const int MaximumSummaryIterations = 8;

        private sealed class Value
        {
            internal readonly HashSet<int> Literals = new HashSet<int>();
            internal readonly HashSet<int> Parameters = new HashSet<int>();

            internal Value Clone()
            {
                var clone = new Value();
                clone.Literals.UnionWith(Literals);
                clone.Parameters.UnionWith(Parameters);
                return clone;
            }

            internal bool Merge(Value other)
            {
                if (other == null) return false;
                int before = Literals.Count + Parameters.Count;
                Literals.UnionWith(other.Literals);
                Parameters.UnionWith(other.Parameters);
                return before != Literals.Count + Parameters.Count;
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

            internal State Clone()
            {
                var clone = new State();
                clone.Stack.AddRange(Stack.Select(value => value.Clone()));
                foreach (KeyValuePair<int, Value> pair in Locals)
                    clone.Locals[pair.Key] = pair.Value.Clone();
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
                return changed;
            }
        }

        private sealed class MethodSummary
        {
            internal readonly Dictionary<int, HashSet<string>> UiRolesByParameter =
                new Dictionary<int, HashSet<string>>();
            internal readonly Dictionary<int, HashSet<string>> NonUiReasonsByParameter =
                new Dictionary<int, HashSet<string>>();
            internal readonly HashSet<int> ReturnParameters = new HashSet<int>();

            internal bool Merge(MethodSummary other)
            {
                if (other == null) return false;
                bool changed = false;
                changed |= MergeMap(UiRolesByParameter, other.UiRolesByParameter);
                changed |= MergeMap(NonUiReasonsByParameter, other.NonUiReasonsByParameter);
                int before = ReturnParameters.Count;
                ReturnParameters.UnionWith(other.ReturnParameters);
                return changed || before != ReturnParameters.Count;
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
            internal readonly Dictionary<int, LiteralFact> Literals = new Dictionary<int, LiteralFact>();
            internal readonly List<string> Diagnostics = new List<string>();
        }

        internal static HardcodedUiIlAnalysisResult Analyze(
            string assemblyPath,
            IEnumerable<HardcodedUiPatchEntry> entries,
            IDictionary<string, HardcodedUiDecisionRecord> existingDecisions = null)
        {
            var output = new HardcodedUiIlAnalysisResult();
            List<HardcodedUiPatchEntry> materialized = (entries ?? Enumerable.Empty<HardcodedUiPatchEntry>())
                .Where(entry => entry != null)
                .ToList();
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                output.Diagnostics.Add("Cecil input assembly is missing: " + assemblyPath);
                return output;
            }

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
                List<MethodDefinition> methods = assembly.Modules
                    .SelectMany(module => EnumerateTypes(module.Types))
                    .SelectMany(type => type.Methods)
                    .Where(method => method != null && method.HasBody)
                    .ToList();
                Dictionary<string, MethodDefinition> methodsByName = methods
                    .GroupBy(method => method.FullName, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                var summaries = methods.ToDictionary(
                    method => method.FullName,
                    method => new MethodSummary(),
                    StringComparer.Ordinal);

                for (int iteration = 0; iteration < MaximumSummaryIterations; iteration++)
                {
                    bool changed = false;
                    foreach (MethodDefinition method in methods)
                    {
                        MethodAnalysis analysis = AnalyzeMethod(method, summaries, methodsByName, false);
                        changed |= summaries[method.FullName].Merge(analysis.Summary);
                    }
                    if (!changed) break;
                }

                Dictionary<int, MethodAnalysis> analysesByToken = new Dictionary<int, MethodAnalysis>();
                foreach (MethodDefinition method in methods)
                {
                    MethodAnalysis analysis = AnalyzeMethod(method, summaries, methodsByName, true);
                    analysesByToken[method.MetadataToken.ToInt32()] = analysis;
                    output.Diagnostics.AddRange(analysis.Diagnostics.Select(diagnostic =>
                        method.FullName + ": " + diagnostic));
                }

                foreach (HardcodedUiPatchEntry entry in materialized)
                {
                    HardcodedUiDecisionRecord existing = null;
                    existingDecisions?.TryGetValue(entry.EntryId, out existing);
                    HardcodedUiDecisionRecord record = existing?.Clone() ?? new HardcodedUiDecisionRecord
                    {
                        EntryId = entry.EntryId,
                        PackageId = entry.PackageId
                    };
                    string fingerprint = HardcodedUiDecisionRecord.CreateAnalysisInputFingerprint(entry);
                    if (!analysesByToken.TryGetValue(entry.MethodMetadataToken, out MethodAnalysis analysis) ||
                        !analysis.Literals.TryGetValue(entry.LiteralOrdinal, out LiteralFact fact))
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
                }
            }
            return output;
        }

        private static MethodAnalysis AnalyzeMethod(
            MethodDefinition method,
            IDictionary<string, MethodSummary> summaries,
            IDictionary<string, MethodDefinition> methodsByName,
            bool collectLiterals)
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
            bool collectLiterals)
        {
            Code code = instruction.OpCode.Code;
            if (code == Code.Ldstr)
            {
                var value = new Value();
                value.Literals.Add(literalOrdinals[instruction]);
                state.Stack.Add(value);
                if (collectLiterals && !analysis.Literals.ContainsKey(literalOrdinals[instruction]))
                    analysis.Literals[literalOrdinals[instruction]] = new LiteralFact();
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

            if (code == Code.Dup)
            {
                state.Stack.Add(state.Stack.Count > 0 ? state.Stack[state.Stack.Count - 1].Clone() : new Value());
                return;
            }
            if (code == Code.Pop) { Pop(state); return; }
            if (code == Code.Ret)
            {
                if (method.ReturnType.MetadataType != MetadataType.Void)
                {
                    Value returned = Pop(state);
                    analysis.Summary.ReturnParameters.UnionWith(returned.Parameters);
                }
                return;
            }
            if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj)
            {
                ExecuteCall(instruction, state, analysis, summaries, methodsByName, collectLiterals);
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
            Instruction instruction,
            State state,
            MethodAnalysis analysis,
            IDictionary<string, MethodSummary> summaries,
            IDictionary<string, MethodDefinition> methodsByName,
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
                    if (IsStringLike(call.Parameters[index].ParameterType))
                        Mark(analysis, arguments[index], true, role, evidence, collectLiterals);
                }
            }
            if (TryGetNonUiReason(declaringType, methodName, out string nonUiReason))
            {
                for (int index = 0; index < arguments.Length; index++)
                {
                    if (IsStringLike(call.Parameters[index].ParameterType))
                        Mark(analysis, arguments[index], false, nonUiReason, evidence, collectLiterals);
                }
            }

            if (summaries.TryGetValue(call.FullName, out MethodSummary summary) &&
                methodsByName.ContainsKey(call.FullName))
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
            if (summaries.TryGetValue(call.FullName, out MethodSummary returnSummary))
            {
                foreach (int parameterIndex in returnSummary.ReturnParameters)
                    if (parameterIndex >= 0 && parameterIndex < arguments.Length)
                        returnValue.Merge(arguments[parameterIndex]);
            }
            if (IsKnownStringPropagation(declaringType, methodName, call.ReturnType))
            {
                returnValue.Merge(instance);
                returnValue.Merge(Value.Union(arguments));
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
            foreach (int literal in value.Literals)
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
            else if (type == "Verse.TooltipHandler" && method.IndexOf("TipRegion", StringComparison.Ordinal) >= 0)
                role = "tooltip";
            else if (type == "Verse.Messages" && method == "Message")
                role = "message";
            else if (type == "Verse.FloatMenuOption" && method == ".ctor")
                role = "button";
            else if ((type == "Verse.Command" || type.EndsWith("Command_Action", StringComparison.Ordinal)) &&
                     (method.StartsWith("set_", StringComparison.Ordinal) || method == ".ctor"))
                role = "label";
            return role.Length > 0;
        }

        private static bool TryGetNonUiReason(string type, string method, out string reason)
        {
            reason = string.Empty;
            if (type == "Verse.Log" || type == "System.Diagnostics.Debug" ||
                type == "System.Diagnostics.Trace")
                reason = "NON_UI_LOG";
            else if (type == "HarmonyLib.AccessTools" || type == "System.Type" ||
                     type.StartsWith("System.Reflection.", StringComparison.Ordinal))
                reason = "NON_UI_REFLECTION_KEY";
            else if (type.StartsWith("Verse.DefDatabase`1", StringComparison.Ordinal) &&
                     method.IndexOf("GetNamed", StringComparison.Ordinal) >= 0)
                reason = "NON_UI_DEF_NAME";
            else if (type == "Verse.Scribe_Values" || type == "Verse.Scribe_Defs" ||
                     type.StartsWith("Newtonsoft.Json", StringComparison.Ordinal))
                reason = "NON_UI_SERIALIZATION_KEY";
            else if (type == "System.IO.File" || type == "System.IO.Directory" ||
                     type == "System.Reflection.Assembly")
                reason = "NON_UI_FILE_PATH";
            return reason.Length > 0;
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
    }
}
