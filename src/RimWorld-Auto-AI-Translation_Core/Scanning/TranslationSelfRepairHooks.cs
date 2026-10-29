using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld.IO;
using Verse;

namespace AutoTranslator_Core
{
    public static class TranslationSelfRepairHooks
    {
        private static bool installed;
        private static readonly HashSet<string> reportedFailures = new HashSet<string>(StringComparer.Ordinal);

        private static bool Recoverable(Exception error) => error is IOException || error is UnauthorizedAccessException ||
            error is Newtonsoft.Json.JsonException || error is ArgumentException || error is MemberAccessException ||
            error is AmbiguousMatchException || error is TargetInvocationException || error is TypeLoadException ||
            error is NotSupportedException || error is InvalidOperationException;

        private static void ReportFailure(string operation, Exception error)
        {
            string message = operation + ": " + error.GetType().Name + ": " + error.Message;
            lock (reportedFailures)
                if (reportedFailures.Add(message))
                    Log.Warning("[AutoTranslationCore] 自動修復暫停；原資料保留，繼續遊戲原有載入流程。" + message);
        }
        public static void Install()
        {
            if (installed) return;
            TranslationRepairController.Install();
            var h = new Harmony("MingYang.AutoTranslation.SelfRepair");
            h.Patch(AccessTools.Method(typeof(UIRoot_Entry), "UIRootUpdate"),
                postfix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(AfterMainMenuUpdate)));
            h.Patch(AccessTools.Method(typeof(LoadedLanguage), "LoadFromFile_Keyed"),
                prefix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(BeforeKeyedRead)));
            h.Patch(AccessTools.Method(typeof(LoadedLanguage), "InjectIntoData_BeforeImpliedDefs"),
                postfix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(AfterEarlyLanguageInjection)));
            h.Patch(AccessTools.Method(typeof(LoadedLanguage), "InjectIntoData_AfterImpliedDefs"),
                postfix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(AfterFinalLanguageInjection)) { priority = Priority.Last });
            h.Patch(AccessTools.Method(typeof(DefInjectionPackage), "AddDataFromFile"),
                prefix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(BeforeRead)),
                postfix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(AfterRead)));
            h.Patch(AccessTools.Method(typeof(DefInjectionPackage), "InjectIntoDefs"),
                prefix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(BeforeInject)),
                postfix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(AfterInject)));
            h.Patch(AccessTools.Method(typeof(DefInjectionPackage), "TryAddFullListInjection"),
                prefix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(BeforeFullList)));
            var clear = AccessTools.Method(typeof(PlayDataLoader), "ClearAllPlayData");
            if (clear != null) h.Patch(clear, postfix: new HarmonyMethod(typeof(TranslationSelfRepairHooks), nameof(AfterClear)));
            installed = true;
        }

        internal static void EnsureConfigGuardInstalled()
        {
            TranslationConfigGuard.EnsureInstalled();
        }

        private static void AfterEarlyLanguageInjection(LoadedLanguage __instance)
            => AutoTranslatorScanner.PrepareNativeStartup(__instance);

        private static void AfterFinalLanguageInjection(LoadedLanguage __instance)
            => AutoTranslatorScanner.TryApplyNativeStartup(__instance);

        private static void AfterMainMenuUpdate(UIRoot_Entry __instance)
        {
            TranslationRepairController.OnMainMenuUpdate(__instance);
        }

        private static void AfterClear()
        {
            AutoTranslatorScanner.InvalidateGameData();
            AutoTranslator_StartupHook.ResetPlayDataLifecycle();
            TranslationRepairController.ClearEnvironment();
            AutoTranslatorScanner.InvalidateSelfRepair();
            lock (reportedFailures) reportedFailures.Clear();
        }

        private static void AfterInject(DefInjectionPackage __instance, bool errorOnDefNotFound)
        {
            if (TranslationConfigGuard.ValidatedScope) return;
            try { PreserveFailedInjections(__instance, errorOnDefNotFound); }
            catch (Exception ex) when (Recoverable(ex)) { ReportFailure("native-result", ex); }
        }

        private static void PreserveFailedInjections(DefInjectionPackage __instance, bool errorOnDefNotFound)
        {
            if (!errorOnDefNotFound) return;
            var failed = new List<KeyValuePair<string, DefInjectionPackage.DefInjection>>();
            foreach (var pair in __instance.injections.ToList())
            {
                var injection = pair.Value;
                if (injection.injected || !AutoTranslatorScanner.IsOwnedTranslationFile(injection.fileSource)) continue;
                var check = new DefTranslationPathResult { Path = pair.Key, Reason = "native_injection_failed", Evidence = "native injected=false after final phase; retained outside retry set" };
                AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, pair.Key,
                    injection.IsFullListInjection ? Newtonsoft.Json.JsonConvert.SerializeObject(injection.fullListInjection) : injection.injection,
                    injection.fileSource, check, injection.IsFullListInjection ? "native-full-list" : "native-result");
                failed.Add(pair);
            }
            // Source files remain intact; persistence is handled off the native load path.
            foreach (var pair in failed)
            {
                AutoTranslatorScanner.RememberFailedNativeCandidate(__instance.defType, pair.Key, pair.Value.injection, pair.Value.fileSource);
                __instance.injections.Remove(pair.Key);
            }
        }

        private static bool BeforeKeyedRead(VirtualFile file)
        {
            // Generated Keyed and DefInjected share the post-load verification boundary.
            // The XML stays on disk for the background payload preparation.
            return file == null || !AutoTranslatorScanner.IsOwnedTranslationFile(file.FullPath);
        }

        private static void BeforeRead(VirtualFile file, ref string preloadedFileContents)
        {
            if (file == null) return;
            preloadedFileContents = RepairReadContent(file.FullPath, preloadedFileContents);
        }

        internal static string RepairReadContent(string path, string input)
        {
            try { return PreserveAndRepairReadContent(path, input); }
            catch (Exception ex) when (Recoverable(ex)) { ReportFailure("read-overlay", ex); return input; }
        }

        private static string PreserveAndRepairReadContent(string path, string input)
        {
            // Preload null means native file IO. Third-party effective content is never rewritten.
            return input;
        }

        private static void AfterRead(DefInjectionPackage __instance, VirtualFile file)
        {
            if (file == null) return;
            foreach (var injection in __instance.injections.Values)
                if (injection.fileSource == file.Name) injection.fileSource = file.FullPath;
        }

        private static bool BeforeFullList(DefInjectionPackage __instance, VirtualFile file, string key, List<string> translation)
        {
            if (file == null || !AutoTranslatorScanner.IsOwnedTranslationFile(file.FullPath)) return true;
            bool duplicate = false;
            try
            {
                string normalized = (string)AccessTools.Method(typeof(DefInjectionPackage), "BackCompatibleKey").Invoke(__instance, new object[] { key });
                if (!__instance.injections.TryGetValue(normalized, out var existing)) return true;
                duplicate = true;
                var incoming = translation ?? new List<string>();
                bool identical = existing.IsFullListInjection && existing.fullListInjection.SequenceEqual(incoming, StringComparer.Ordinal);
                var journal = AutoTranslatorScanner.RepairJournal;
                // Source XML is retained; native hooks never perform backup IO.
                string selectedSource = existing.fileSource == file.Name ? file.FullPath : existing.fileSource;

                Action<string, string, string> preserve = (source, value, status) => journal.Record(new TranslationRepairRecord {
                    SourceFile = source, SourceKind = "native-duplicate-list", DefType = __instance.defType.FullName,
                    OriginalKey = key, NewKey = normalized, Translation = value, GameVersion = AutoTranslatorScanner.CurrentRimWorldVersion,
                    Reason = "duplicate_full_list", Status = status,
                    Evidence = identical ? "identical native full-list candidate retained once" :
                        "conflicting native list retained for review; first candidate stays selected; later XML entries continue loading"
                });
                preserve(selectedSource, existing.IsFullListInjection ? Newtonsoft.Json.JsonConvert.SerializeObject(existing.fullListInjection) : existing.injection,
                    identical ? "相同譯文合併" : "保留候選");
                preserve(file.FullPath, Newtonsoft.Json.JsonConvert.SerializeObject(incoming), identical ? "相同譯文合併" : "衝突");
                // Checkpoint is performed by the background pipeline; both source files remain untouched.
                return false;
            }
            catch (Exception ex) when (Recoverable(ex)) { ReportFailure("native-full-list", ex); return !duplicate; }
        }

        private static void BeforeInject(DefInjectionPackage __instance, bool errorOnDefNotFound)
        {
            if (TranslationConfigGuard.ValidatedScope) return;
            // Remove owned candidates before ANY diagnostic IO. Post-load pipeline owns their publication.
            foreach (var pair in __instance.injections.ToList())
            {
                if (!pair.Value.injected && AutoTranslatorScanner.IsOwnedTranslationFile(pair.Value.fileSource))
                {
                    __instance.injections.Remove(pair.Key);
                    continue;
                }
                if (!errorOnDefNotFound || pair.Value.injected) continue;
                // Third-party effective text is never rewritten. Only proven unsafe
                // native traversal candidates are omitted from this execution.
                var check = pair.Value.IsFullListInjection
                    ? AutoTranslatorScanner.ValidateLoadedDefList(__instance.defType, pair.Key)
                    : AutoTranslatorScanner.ValidateLoadedDefTranslation(__instance.defType, pair.Key);
                if (check.Reason != "missing_handle" && check.Reason != "index_out_of_bounds" && check.Reason != "null_reference") continue;
                __instance.injections.Remove(pair.Key);
                try
                {
                    AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, pair.Key,
                        pair.Value.IsFullListInjection ? Newtonsoft.Json.JsonConvert.SerializeObject(pair.Value.fullListInjection) : pair.Value.injection,
                        pair.Value.fileSource, check, "third-party-diagnostic");
                }
                catch (Exception ex) when (Recoverable(ex)) { ReportFailure("third-party-safety", ex); }
            }
        }

        private static void PrepareNativeInjections(DefInjectionPackage __instance, bool errorOnDefNotFound)
        {
            // Stage changes separately. A preservation failure must leave the
            // original dictionary and injection objects available to upstream.
            var planned = new Dictionary<string, DefInjectionPackage.DefInjection>(__instance.injections, __instance.injections.Comparer);
            var renames = new List<KeyValuePair<DefInjectionPackage.DefInjection, string>>();
            var textChanges = new List<KeyValuePair<DefInjectionPackage.DefInjection, string>>();
            var canonical = new Dictionary<string, DefInjectionPackage.DefInjection>(StringComparer.Ordinal);
            foreach (var pair in __instance.injections.ToList())
            {
                var injection = pair.Value;
                if (!AutoTranslatorScanner.IsOwnedTranslationFile(injection.fileSource)) continue;
                if (!injection.injected && AutoTranslatorScanner.IsFailedNativeCandidate(__instance.defType, pair.Key,
                    injection.IsFullListInjection ? Newtonsoft.Json.JsonConvert.SerializeObject(injection.fullListInjection) : injection.injection,
                    injection.fileSource))
                {
                    planned.Remove(pair.Key);
                    continue;
                }
                // Implied Defs can legitimately be absent in the first phase.
                var check = AutoTranslatorScanner.ValidateLoadedDefTranslation(__instance.defType, pair.Key);
                if (check.Reason == "missing_def" && !errorOnDefNotFound) continue;
                if (injection.IsFullListInjection)
                {
                    // Validate full-list semantics separately; never flatten or
                    // silently discard a native TranslationCanChangeCount list.
                    check = AutoTranslatorScanner.ValidateLoadedDefList(__instance.defType, pair.Key);
                }
                else
                {
                    string value = AutoTranslatorScanner.PrepareDefTranslationText(check, injection.injection);
                    if (value != injection.injection)
                    {
                        AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, pair.Key, injection.injection,
                            injection.fileSource, check, "native-value", appliedValue: value);
                        textChanges.Add(new KeyValuePair<DefInjectionPackage.DefInjection, string>(injection, value));
                    }
                }
                if (!check.Allowed)
                {
                    AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, pair.Key,
                        injection.IsFullListInjection ? Newtonsoft.Json.JsonConvert.SerializeObject(injection.fullListInjection) : injection.injection,
                        injection.fileSource, check, injection.IsFullListInjection ? "native-full-list" : "native-generated");
                    planned.Remove(pair.Key);
                    continue;
                }
                if (check.Path != pair.Key && planned.TryGetValue(check.Path, out var existingTarget))
                {
                    AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, pair.Key, injection.injection,
                        injection.fileSource, check, "native-alias", existingTarget.injection == injection.injection ? "相同譯文合併" : "衝突");
                    AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, existingTarget.path, existingTarget.injection,
                        existingTarget.fileSource, check, "native-selected", "保留候選");
                    planned.Remove(pair.Key);
                    continue;
                }
                if (canonical.TryGetValue(check.ResolvedPath, out var first))
                {
                    AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, pair.Key, injection.injection,
                        injection.fileSource, check, "native-generated", first.injection == injection.injection ? "相同譯文合併" : "衝突");
                    AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, first.path, first.injection,
                        first.fileSource, check, "native-selected", "保留候選");
                    planned.Remove(pair.Key);
                    continue;
                }
                canonical[check.ResolvedPath] = injection;
                if (check.Path != pair.Key)
                {
                    AutoTranslatorScanner.RecordDefRepair(__instance.defType.FullName, pair.Key, injection.injection,
                        injection.fileSource, check, "native-generated");
                    if (!planned.ContainsKey(check.Path))
                    {
                        planned.Remove(pair.Key);
                        planned[check.Path] = injection;
                        renames.Add(new KeyValuePair<DefInjectionPackage.DefInjection, string>(injection, check.Path));
                    }
                }
            }
            AutoTranslatorScanner.FlushSelfRepair();
            foreach (var change in textChanges) { change.Key.injection = change.Value; change.Key.injected = false; }
            foreach (var rename in renames) rename.Key.path = rename.Value;
            __instance.injections = planned;
            // Keep native diagnostics; changed text goes through real injection.
        }
    }
}
