using AutoTranslator_Core.TargetedHardcodedUi;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HardcodedUiTargetedPatchScanner
{
    internal static class Program
    {
        private const string SupportedCallDeclaringType = "Verse.Widgets";
        private const string SupportedCallMethodName = "Label";
        private const string SupportedCallSignature = "Verse.Widgets::Label(UnityEngine.Rect,System.String)->System.Void";

        private static int Main(string[] args)
        {
            if (args == null || args.Length < 2)
            {
                Console.Error.WriteLine("Usage: scanner <assembly.dll> <manifest.json> [--package-id id] [--relative-path path] [--approve] [--translation text] [--target-folder folder]");
                return 2;
            }

            string assemblyPath = Path.GetFullPath(args[0]);
            string outputPath = Path.GetFullPath(args[1]);
            if (!File.Exists(assemblyPath))
            {
                Console.Error.WriteLine("Assembly not found: " + assemblyPath);
                return 2;
            }

            string packageId = GetOption(args, "--package-id", HardcodedUiTargetedPatchManagerPlaceholder.ApprovedFixturePackageId);
            string relativePath = GetOption(args, "--relative-path", Path.GetFileName(assemblyPath));
            string translation = GetOption(args, "--translation", string.Empty);
            string targetFolder = GetOption(args, "--target-folder", "ChineseTraditional");
            bool approved = HasFlag(args, "--approve");

            HardcodedUiPatchManifest manifest = new HardcodedUiPatchManifest
            {
                Approved = approved
            };
            List<string> diagnostics = new List<string>();

            try
            {
                using (ModuleDefinition module = ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters
                {
                    InMemory = true,
                    ReadingMode = ReadingMode.Deferred,
                    ReadSymbols = false
                }))
                {
                    Assembly reflectionOnly = Assembly.ReflectionOnlyLoadFrom(assemblyPath);
                    foreach (TypeDefinition type in GetTypes(module.Types))
                    {
                        if (IsGenerated(type)) continue;
                        foreach (MethodDefinition method in type.Methods)
                        {
                            if (!method.HasBody || IsGenerated(method)) continue;
                            ScanMethod(method, reflectionOnly, packageId, relativePath, translation, targetFolder, manifest, diagnostics);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Scan failed: " + ex);
                return 1;
            }

            manifest.Entries = manifest.Entries
                .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
                .ToList();
            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

            Console.WriteLine("Candidates: " + manifest.Entries.Count);
            Console.WriteLine("Approved: " + manifest.Approved);
            foreach (string diagnostic in diagnostics) Console.WriteLine(diagnostic);
            foreach (HardcodedUiPatchEntry entry in manifest.Entries)
            {
                Console.WriteLine(entry.EntryId + " | " + entry.MethodSignature + " | " + entry.Literal);
            }
            return 0;
        }

        private static void ScanMethod(
            MethodDefinition method,
            Assembly reflectionOnly,
            string packageId,
            string relativePath,
            string translation,
            string targetFolder,
            HardcodedUiPatchManifest manifest,
            List<string> diagnostics)
        {
            int literalOrdinal = -1;
            IList<Instruction> instructions = method.Body.Instructions;
            for (int index = 0; index < instructions.Count; index++)
            {
                Instruction instruction = instructions[index];
                if (instruction.OpCode != OpCodes.Ldstr || !(instruction.Operand is string)) continue;
                literalOrdinal++;

                int next = index + 1;
                while (next < instructions.Count && instructions[next].OpCode == OpCodes.Nop) next++;
                if (next >= instructions.Count || !IsSupportedCall(instructions[next].Operand)) continue;

                string literal = (string)instruction.Operand;
                if (string.IsNullOrWhiteSpace(literal) || literal.Length > 512) continue;
                string methodSignature = GetMethodSignature(method);
                int token = method.MetadataToken.ToInt32();
                string fingerprint = GetReflectionOnlyFingerprint(reflectionOnly, token);
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    diagnostics.Add("Skipped fingerprint-unavailable method: " + methodSignature);
                    continue;
                }

                HardcodedUiPatchEntry entry = new HardcodedUiPatchEntry
                {
                    EntryId = HardcodedUiMethodIdentity.CreateEntryId(packageId, relativePath, methodSignature, literalOrdinal, literal),
                    PackageId = packageId,
                    AssemblyRelativePath = HardcodedUiMethodIdentity.NormalizeRelativePath(relativePath),
                    AssemblySha256 = HardcodedUiMethodIdentity.ComputeFileSha256(method.Module.FileName),
                    AssemblyMvid = method.Module.Mvid.ToString("D"),
                    DeclaringType = GetTypeName(method.DeclaringType),
                    MethodName = method.Name,
                    MethodSignature = methodSignature,
                    MethodMetadataToken = token,
                    MethodIlFingerprint = fingerprint,
                    Literal = literal,
                    LiteralOrdinal = literalOrdinal,
                    CallDeclaringType = SupportedCallDeclaringType,
                    CallMethodName = SupportedCallMethodName,
                    CallSignature = SupportedCallSignature
                };
                if (!string.IsNullOrWhiteSpace(translation)) entry.Translations[targetFolder] = translation;
                manifest.Entries.Add(entry);
            }
        }

        private static string GetReflectionOnlyFingerprint(Assembly assembly, int metadataToken)
        {
            try
            {
                MethodBase method = assembly.ManifestModule.ResolveMethod(metadataToken);
                return HardcodedUiMethodIdentity.ComputeMethodIlFingerprint(method);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsSupportedCall(object operand)
        {
            MethodReference method = operand as MethodReference;
            if (method == null || method.Name != SupportedCallMethodName || method.DeclaringType == null) return false;
            if (!string.Equals(method.DeclaringType.FullName, SupportedCallDeclaringType, StringComparison.Ordinal)) return false;
            return method.Parameters.Count == 2 &&
                string.Equals(GetTypeName(method.Parameters[0].ParameterType), "UnityEngine.Rect", StringComparison.Ordinal) &&
                string.Equals(GetTypeName(method.Parameters[1].ParameterType), "System.String", StringComparison.Ordinal);
        }

        private static IEnumerable<TypeDefinition> GetTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition type in types ?? Enumerable.Empty<TypeDefinition>())
            {
                yield return type;
                foreach (TypeDefinition nested in GetTypes(type.NestedTypes)) yield return nested;
            }
        }

        private static bool IsGenerated(TypeDefinition type)
        {
            return type == null || type.Name.IndexOf('<') >= 0 ||
                type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        private static bool IsGenerated(MethodDefinition method)
        {
            return method == null || method.Name.IndexOf('<') >= 0 ||
                method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        private static string GetMethodSignature(MethodDefinition method)
        {
            string genericSuffix = method.GenericParameters != null && method.GenericParameters.Count > 0
                ? "`" + method.GenericParameters.Count.ToString()
                : string.Empty;
            return GetTypeName(method.DeclaringType) + "::" + method.Name + genericSuffix + "(" +
                string.Join(",", method.Parameters.Select(parameter => GetTypeName(parameter.ParameterType)).ToArray()) +
                ")->" + GetTypeName(method.ReturnType);
        }

        private static string GetTypeName(TypeReference type)
        {
            if (type == null) return string.Empty;
            ByReferenceType byReference = type as ByReferenceType;
            if (byReference != null) return GetTypeName(byReference.ElementType) + "&";
            PointerType pointer = type as PointerType;
            if (pointer != null) return GetTypeName(pointer.ElementType) + "*";
            ArrayType array = type as ArrayType;
            if (array != null) return GetTypeName(array.ElementType) + "[" + new string(',', Math.Max(0, array.Rank - 1)) + "]";
            GenericInstanceType generic = type as GenericInstanceType;
            if (generic != null)
            {
                return GetTypeName(generic.ElementType) + "<" +
                    string.Join(",", generic.GenericArguments.Select(GetTypeName).ToArray()) + ">";
            }
            return (type.FullName ?? string.Empty).Replace('/', '+');
        }

        private static string GetOption(string[] args, string name, string fallback)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return fallback;
        }

        private static bool HasFlag(string[] args, string name)
        {
            return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        }

        // The scanner is intentionally independent from the game assembly. Keep
        // the fixture-only default here; runtime has the same hard allow-list.
        private static class HardcodedUiTargetedPatchManagerPlaceholder
        {
            public const string ApprovedFixturePackageId = "atc.hardcodedui.fixture";
        }
    }
}
