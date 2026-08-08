using AutoTranslator_Core.TargetedHardcodedUi;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Reflection;

namespace HardcodedUiTargetedPatchSelfTest
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args == null || args.Length < 2)
            {
                Console.Error.WriteLine("Usage: selftest <fixture.dll> <manifest.json>");
                return 2;
            }

            string fixturePath = Path.GetFullPath(args[0]);
            string manifestPath = Path.GetFullPath(args[1]);
            HardcodedUiPatchManifest manifest = JsonConvert.DeserializeObject<HardcodedUiPatchManifest>(
                File.ReadAllText(manifestPath));
            if (manifest == null || manifest.Entries == null || manifest.Entries.Count != 1)
                return Fail("scanner must produce exactly one candidate");

            HardcodedUiPatchEntry entry = manifest.Entries[0];
            byte[] before = File.ReadAllBytes(fixturePath);
            Assembly fixture = Assembly.LoadFrom(fixturePath);
            Type windowType = fixture.GetType("Atc.HardcodedUiFixture.FixtureWindow", true);
            MethodInfo method = windowType.GetMethod("DoWindowContents", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return Fail("fixture method missing");

            AssertEqual(entry.MethodSignature, HardcodedUiMethodIdentity.GetMethodSignature(method), "method signature");
            AssertEqual(entry.MethodMetadataToken, method.MetadataToken, "method metadata token");
            AssertEqual(entry.AssemblyMvid, fixture.ManifestModule.ModuleVersionId.ToString("D"), "assembly MVID");
            AssertEqual(entry.MethodIlFingerprint, HardcodedUiMethodIdentity.ComputeMethodIlFingerprint(method), "method IL fingerprint");
            AssertEqual(entry.AssemblySha256, HardcodedUiMethodIdentity.ComputeFileSha256(fixturePath), "assembly hash");
            if (!HardcodedUiMethodIdentity.IsDeterministicEntryId(
                entry.EntryId,
                entry.PackageId,
                entry.AssemblyRelativePath,
                entry.MethodSignature,
                entry.LiteralOrdinal,
                entry.Literal))
                return Fail("scanner entry id is not deterministic");

            string alteredEntryId = HardcodedUiMethodIdentity.CreateEntryId(
                entry.PackageId,
                entry.AssemblyRelativePath,
                entry.MethodSignature,
                entry.LiteralOrdinal + 1,
                entry.Literal);
            if (HardcodedUiMethodIdentity.IsDeterministicEntryId(
                alteredEntryId,
                entry.PackageId,
                entry.AssemblyRelativePath,
                entry.MethodSignature,
                entry.LiteralOrdinal,
                entry.Literal))
                return Fail("altered entry id was accepted");

            string duplicateId;
            if (!HardcodedUiMethodIdentity.TryFindDuplicateEntryId(
                new[] { entry.EntryId, entry.EntryId }, out duplicateId) ||
                !string.Equals(duplicateId, entry.EntryId, StringComparison.Ordinal))
                return Fail("duplicate entry id was not detected");

            string targetA = HardcodedUiMethodIdentity.CreateMethodTargetIdentity(
                entry.PackageId,
                entry.AssemblyRelativePath,
                entry.AssemblySha256,
                entry.AssemblyMvid,
                entry.MethodSignature);
            string targetB = HardcodedUiMethodIdentity.CreateMethodTargetIdentity(
                entry.PackageId,
                entry.AssemblyRelativePath + ".copy",
                entry.AssemblySha256,
                entry.AssemblyMvid,
                entry.MethodSignature);
            if (string.Equals(targetA, targetB, StringComparison.Ordinal))
                return Fail("different assembly paths share a target identity");

            string runtimeMethodIdentity = HardcodedUiMethodIdentity.GetRuntimeMethodIdentity(method);
            if (string.IsNullOrWhiteSpace(runtimeMethodIdentity))
                return Fail("runtime method identity is empty");

            HardcodedUiTargetedPatchTranspiler.SetSpecs(method, new[]
            {
                new HardcodedUiTranspileSpec
                {
                    EntryId = entry.EntryId,
                    Literal = entry.Literal,
                    LiteralOrdinal = entry.LiteralOrdinal
                }
            });
            HardcodedUiRuntime.ReplaceSnapshot(new System.Collections.Generic.Dictionary<string, string>
            {
                [entry.EntryId] = "硬編碼測試標籤"
            },
            new System.Collections.Generic.Dictionary<string, string>
            {
                [entry.EntryId] = entry.Literal
            });

            Harmony harmony = new Harmony("ATC.HardcodedUiTargetedPatchSelfTest");
            harmony.Patch(method, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(HardcodedUiTargetedPatchTranspiler), nameof(HardcodedUiTargetedPatchTranspiler.Transpile))));

            object window = Activator.CreateInstance(windowType);
            Type rectType = fixture.GetType("UnityEngine.Rect", true);
            object rect = Activator.CreateInstance(rectType);
            method.Invoke(window, new[] { rect });
            Type widgetsType = fixture.GetType("Verse.Widgets", true);
            FieldInfo lastLabel = widgetsType.GetField("LastLabel", BindingFlags.Public | BindingFlags.Static);
            AssertEqual("硬編碼測試標籤", (string)lastLabel.GetValue(null), "targeted translation");

            HardcodedUiRuntime.ReplaceSnapshot(new System.Collections.Generic.Dictionary<string, string>
            {
                [entry.EntryId] = "第二版標籤"
            },
            new System.Collections.Generic.Dictionary<string, string>
            {
                [entry.EntryId] = entry.Literal
            });
            method.Invoke(window, new[] { rect });
            AssertEqual("第二版標籤", (string)lastLabel.GetValue(null), "hot snapshot reload");
            AssertEqual("wrong source", HardcodedUiRuntime.Resolve("wrong source", entry.EntryId), "source identity guard");

            HardcodedUiRuntime.ClearSnapshot();
            method.Invoke(window, new[] { rect });
            AssertEqual(entry.Literal, (string)lastLabel.GetValue(null), "missing translation fallback");

            long resolveBeforeLoop = HardcodedUiRuntime.ResolveCount;
            for (int i = 0; i < 1000; i++)
            {
                AssertEqual(entry.Literal, HardcodedUiRuntime.Resolve(entry.Literal, entry.EntryId), "resolver fallback loop");
            }
            AssertEqual(resolveBeforeLoop + 1000L, HardcodedUiRuntime.ResolveCount, "resolver call count");

            MethodInfo logMethod = windowType.GetMethod("WriteLogOnly", BindingFlags.Public | BindingFlags.Instance);
            long beforeResolve = HardcodedUiRuntime.ResolveCount;
            logMethod.Invoke(window, null);
            AssertEqual(beforeResolve, HardcodedUiRuntime.ResolveCount, "non-UI literal is untouched");

            byte[] after = File.ReadAllBytes(fixturePath);
            if (before.Length != after.Length) return Fail("fixture assembly length changed");
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] != after[i]) return Fail("fixture assembly bytes changed");
            }

            harmony.Unpatch(method, HarmonyPatchType.Transpiler, "ATC.HardcodedUiTargetedPatchSelfTest");
            HardcodedUiTargetedPatchTranspiler.RemoveSpecs(method);
            Console.WriteLine("PASS: targeted translation, hot reload, fallback, non-UI skip, identity validation, and DLL immutability");
            return 0;
        }

        private static void AssertEqual<T>(T expected, T actual, string name)
        {
            if (!object.Equals(expected, actual)) throw new InvalidOperationException(
                name + " mismatch. expected=" + expected + ", actual=" + actual);
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine("FAIL: " + message);
            return 1;
        }
    }
}
