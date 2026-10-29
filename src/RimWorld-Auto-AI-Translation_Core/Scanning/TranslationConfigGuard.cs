using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AutoTranslator_Core
{
    // Only the Def being changed is checked, on the game thread. No database scan
    // or disk IO is performed for a successful candidate.
    internal static class TranslationConfigGuard
    {
        [ThreadStatic] private static int validatedScope;
        private static readonly MethodInfo NativeSetter = AccessTools.Method(typeof(DefInjectionPackage), "SetDefFieldAtPath");
        private static bool installed;
        internal static bool IsInstalled => installed;
        internal static bool ValidatedScope => validatedScope > 0;

        internal static void EnsureInstalled()
        {
            if (installed) return;
            if (NativeSetter == null) throw new MissingMethodException("DefInjectionPackage.SetDefFieldAtPath");
            installed = true;
        }

        internal static void ApplyDerivedText(Type databaseType, Def root, string field, string text)
        {
            if ((string)AccessTools.Field(root.GetType(), field).GetValue(root) == text) return;
            var package = new DefInjectionPackage(databaseType);
            string path = root.defName + "." + field;
            package.injections[path] = new DefInjectionPackage.DefInjection {
                path = path, injection = text, fileSource = "ATC:derived-normalization"
            };
            ApplyValidated(package);
        }

        internal static void ApplyValidated(DefInjectionPackage package)
        {
            EnsureInstalled();
            validatedScope++;
            try
            {
                // InjectIntoDefs clears caches for the entire Def database. A
                // repair isolates one entry. Run our guard explicitly around the
                // native leaf setter, without registering a global setter patch:
                // VisualExceptions attributes even earlier third-party exceptions
                // to every patch registered when its report is opened.
                foreach (var pair in package.injections)
                {
                    var entry = pair.Value;
                    if (entry.injected) continue;
                    object[] args = { package.defType, pair.Key,
                        entry.IsFullListInjection ? (object)entry.fullListInjection : entry.injection,
                        entry.IsFullListInjection ? typeof(List<string>) : typeof(string), true,
                        entry.fileSource, entry.isPlaceholder, null, null, null, null };
                    bool result = false;
                    if (Before(package.defType, pair.Key, args[2], (Type)args[3], entry.fileSource,
                        entry.isPlaceholder, ref result, out var snapshot))
                    {
                        try
                        {
                            if (snapshot != null && snapshot.Slots.Any(slot => slot.Parent.GetType().IsValueType))
                            {
                                int last = snapshot.Slots.Count - 1;
                                snapshot.Slots[last].Restore(args[2]);
                                for (int i = last; i > 0 && snapshot.Slots[i].Parent.GetType().IsValueType; i--)
                                    snapshot.Slots[i - 1].Restore(snapshot.Slots[i].Parent);
                                args[7] = snapshot.ResolvedPath;
                                args[9] = snapshot.Slots[last].Value as string;
                                result = true;
                            }
                            else result = (bool)NativeSetter.Invoke(package, args);
                        }
                        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException ||
                            error.InnerException is NullReferenceException || error.InnerException is IndexOutOfRangeException ||
                            error.InnerException is ArgumentException)
                        {
                            // A native/mod setter can fail after touching the leaf.
                            // Restore it before preserving this single candidate;
                            // remaining translations still get their own checks.
                            result = false;
                            After(ref result, snapshot);
                            Reject(snapshot ?? new Snapshot { Type = package.defType, Path = pair.Key,
                                Source = entry.fileSource, FullList = entry.IsFullListInjection,
                                Text = entry.IsFullListInjection ? Newtonsoft.Json.JsonConvert.SerializeObject(entry.fullListInjection) : entry.injection },
                                "native_injection_failed", error.InnerException.GetType().Name + ": " + error.InnerException.Message);
                            continue;
                        }
                        After(ref result, snapshot);
                    }
                    entry.injected = result;
                    entry.normalizedPath = args[7] as string;
                    entry.suggestedPath = args[8] as string;
                    entry.replacedString = args[9] as string;
                    entry.replacedList = args[10] as IEnumerable<string>;
                    if (entry.injected)
                    {
                        string name = (entry.normalizedPath ?? pair.Key).Split('.')[0];
                        name = BackCompatibility.BackCompatibleDefName(package.defType, name, true);
                        GenDefDatabase.GetDefSilentFail(package.defType, name, false)?.ClearCachedData();
                    }
                }
            }
            finally { validatedScope--; }
        }

        internal sealed class Snapshot
        {
            internal Def Root;
            internal List<Slot> Slots;
            internal Type Type;
            internal string Path, ResolvedPath, Source, Text;
            internal bool FullList;
        }

        internal sealed class Slot
        {
            internal object Parent, Value;
            internal FieldInfo Field;
            internal PropertyInfo Item;
            internal int Index;
            internal void Restore(object value)
            {
                if (Field != null) Field.SetValue(Parent, value);
                else Item.SetValue(Parent, value, new object[] { Index });
            }
        }

        private static bool Before(Type defType, string path, object value, Type ensureFieldType, string fileSource,
            bool isPlaceholder, ref bool __result, out Snapshot __state)
        {
            __state = null;
            if (!AutoTranslatorScanner.StructureReady && ValidatedScope) { __result = false; return false; }
            if (isPlaceholder || !AutoTranslatorScanner.StructureReady ||
                (!ValidatedScope && !AutoTranslatorScanner.IsOwnedTranslationFile(fileSource))) return true;
            bool fullList = ensureFieldType == typeof(List<string>);
            var check = AutoTranslatorScanner.ValidateLoadedDefTranslation(defType, path, fullList);
            // Recheck immediately before mutation: an optional/list container may
            // have changed since the repair's earlier validation phase.
            if (!check.Allowed && ValidatedScope)
            {
                AutoTranslatorScanner.RecordDefRepair(defType.FullName, path,
                    fullList ? Newtonsoft.Json.JsonConvert.SerializeObject(value) : value as string,
                    fileSource, check, fullList ? "native-config-list" : "native-config");
                __result = false;
                return false;
            }
            if (!check.Allowed) return true;
            var state = new Snapshot { Type = defType, Path = path, Source = fileSource, FullList = fullList,
                Text = fullList ? Newtonsoft.Json.JsonConvert.SerializeObject(value) : value as string };
            if (AutoTranslatorScanner.IsFailedNativeCandidate(defType, path, state.Text, fileSource))
            { __result = false; return false; }
            try
            {
                state.Root = GenDefDatabase.GetDefSilentFail(defType, check.ResolvedPath.Split('.')[0], false);
                state.Slots = CaptureSlots(state.Root, check.ResolvedPath);
                state.ResolvedPath = check.ResolvedPath;
                __state = state;
                return true;
            }
            catch (Exception error)
            {
                Reject(state, "config_validation_unavailable", error.GetType().Name + ": " + error.Message);
                __result = false;
                return false;
            }
        }

        private static void After(ref bool __result, Snapshot __state)
        {
            if (__state == null) return;
            string reason = null, evidence = null;
            if (__result)
            {
                try
                {
                    object actual = DefTranslationPathValidator.ReadValidatedValue(__state.Root, __state.ResolvedPath);
                    bool matches = __state.FullList
                        ? actual is IEnumerable<string> list && Newtonsoft.Json.JsonConvert.SerializeObject(list) == __state.Text
                        : string.Equals(actual as string, __state.Text, StringComparison.Ordinal);
                    if (matches) return;
                    reason = "readback_mismatch";
                    evidence = "Native setter did not publish the requested value.";
                }
                catch (Exception error)
                {
                    reason = "config_validation_unavailable";
                    evidence = error.GetType().Name + ": " + error.Message;
                }
            }
            // Restore the actual old leaf, including the original List<string>
            // reference. Propagate boxed structs back through their parent slots.
            int last = __state.Slots.Count - 1;
            __state.Slots[last].Restore(__state.Slots[last].Value);
            for (int i = last; i > 0 && __state.Slots[i].Parent.GetType().IsValueType; i--)
                __state.Slots[i - 1].Restore(__state.Slots[i].Parent);
            __result = false; // enclosing native loop writes injected=false
            __state.Root.ClearCachedData();
            if (reason != null) Reject(__state, reason, evidence);
        }

        private static List<Slot> CaptureSlots(Def root, string path)
        {
            object current = root;
            var result = new List<Slot>();
            foreach (string part in path.Split('.').Skip(1))
            {
                var slot = new Slot { Parent = current };
                slot.Field = AccessTools.Field(current.GetType(), part);
                if (slot.Field != null) slot.Value = slot.Field.GetValue(current);
                else
                {
                    slot.Index = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
                    slot.Item = current.GetType().GetProperty("Item", new[] { typeof(int) });
                    slot.Value = slot.Item.GetValue(current, new object[] { slot.Index });
                }
                result.Add(slot);
                current = slot.Value;
            }
            return result;
        }

        private static void Reject(Snapshot state, string reason, string evidence)
        {
            var check = new DefTranslationPathResult { Path = state.Path, Reason = reason,
                Evidence = "Translation not applied; runtime value retained. " + evidence };
            AutoTranslatorScanner.RecordDefRepair(state.Type.FullName, state.Path, state.Text, state.Source, check,
                state.FullList ? "native-config-list" : "native-config");
            if (reason != "config_validation_unavailable" && reason != "validation_unavailable")
                AutoTranslatorScanner.RememberFailedNativeCandidate(state.Type, state.Path, state.Text, state.Source);
        }
    }
}
