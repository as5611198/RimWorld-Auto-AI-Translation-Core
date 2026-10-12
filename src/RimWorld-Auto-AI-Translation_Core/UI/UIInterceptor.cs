using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;
using AutoTranslator_Core.TargetedHardcodedUi;

namespace AutoTranslator_Core
{
    [StaticConstructorOnStartup]
    public static partial class UIInterceptor
    {
        private static readonly object SourceOwnerMapLock = new object();
        private static List<SourceOwnerRoot> _sourceOwnerRoots = new List<SourceOwnerRoot>();
        private static int _sourceOwnerRootModCount = -1;

        static UIInterceptor()
        {
            // Only install observation hooks. There are no legacy workers or model queues.
            new Harmony("MingYang.AutoTranslation.UIInterceptor").PatchAll(typeof(UIInterceptor).Assembly);
        }

        internal static void ObserveDisplayedText(string text, MethodBase sourceMethod = null)
        {
            if (AutoTranslatorMod.Settings?.EnableUIInterceptor != true ||
                string.IsNullOrWhiteSpace(text) ||
                ShouldBypassUIPatchText(text) || HardcodedUiRuntime.IsAppliedTranslationText(text) ||
                !HardcodedUiRuntimeObservationStore.ShouldInspectStack(text)) return;
            SourceOwnerResolution source = sourceMethod == null
                ? ResolveSourceOwnerFromStack()
                : ResolveSourceOwnerFromMethod(sourceMethod, GetSourceOwnerRoots());
            if (source == null || string.IsNullOrWhiteSpace(source.PackageId)) return;
            HardcodedUiRuntimeObservationStore.Record(
                source.PackageId, source.AssemblyRelativePath, source.Method, text);
        }

        public static void ResetProbeCaches()
        {
            FastBypassDecisionCache.Clear();
            lock (SourceOwnerMapLock)
            {
                _sourceOwnerRootModCount = -1;
                _sourceOwnerRoots.Clear();
            }
        }

        public static void PrepareForLanguageChange()
        {
            HardcodedUiRuntimeObservationStore.Flush();
            ResetProbeCaches();
        }

        public static void ReloadForLanguageChange()
        {
            ResetProbeCaches();
            HardcodedUiRuntimeObservationStore.BeginSession();
        }

        private static SourceOwnerResolution ResolveSourceOwnerFromStack()
        {
            try
            {
                List<SourceOwnerRoot> roots = GetSourceOwnerRoots();
                StackFrame[] frames = new StackTrace(2, false).GetFrames();
                if (frames == null) return null;

                foreach (StackFrame frame in frames)
                {
                    MethodBase method;
                    try
                    {
                        // Mono can attribute patched frames to MonoMod's dynamic wrapper
                        // in 0Harmony.dll. Recover the original before resolving its Mod.
                        method = Harmony.GetOriginalMethodFromStackframe(frame);
                    }
                    catch { continue; }
                    SourceOwnerResolution source = ResolveSourceOwnerFromMethod(method, roots);
                    if (source != null) return source;
                }
            }
            catch
            {
            }
            return null;
        }

        private static SourceOwnerResolution ResolveSourceOwnerFromMethod(
            MethodBase method, List<SourceOwnerRoot> roots)
        {
            try
            {
                Assembly assembly = method?.DeclaringType?.Assembly;
                if (assembly == null || assembly == typeof(UIInterceptor).Assembly) return null;
                if ((method.MetadataToken & unchecked((int)0xFF000000)) != 0x06000000) return null;
                if (string.IsNullOrWhiteSpace(assembly.Location)) return null;
                string location = Path.GetFullPath(assembly.Location);
                SourceOwnerRoot match = roots.FirstOrDefault(root => IsPathUnderRoot(location, root.Root));
                if (match == null) return null;
                return new SourceOwnerResolution
                {
                    PackageId = match.PackageId,
                    AssemblyRelativePath = location.Substring(match.Root.Length),
                    Method = method
                };
            }
            catch { return null; }
        }

        private static List<SourceOwnerRoot> GetSourceOwnerRoots()
        {
            List<ModContentPack> running = LoadedModManager.RunningModsListForReading?.ToList() ?? new List<ModContentPack>();
            if (_sourceOwnerRootModCount == running.Count) return _sourceOwnerRoots;

            lock (SourceOwnerMapLock)
            {
                if (_sourceOwnerRootModCount == running.Count) return _sourceOwnerRoots;
                _sourceOwnerRoots = running
                    .Where(mod => mod != null && !string.IsNullOrWhiteSpace(mod.RootDir) && !string.IsNullOrWhiteSpace(mod.PackageId))
                    .Select(mod => new SourceOwnerRoot
                    {
                        PackageId = mod.PackageId.Trim().ToLowerInvariant(),
                        Root = NormalizeRoot(mod.RootDir)
                    })
                    .Where(root => !string.IsNullOrWhiteSpace(root.Root))
                    .OrderByDescending(root => root.Root.Length)
                    .ToList();
                _sourceOwnerRootModCount = running.Count;
                return _sourceOwnerRoots;
            }
        }

        private static string NormalizeRoot(string path)
        {
            try
            {
                return Path.GetFullPath(path ?? string.Empty)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch { return string.Empty; }
        }

        private static bool IsPathUnderRoot(string path, string root)
        {
            return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(root) &&
                   path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class SourceOwnerRoot
        {
            public string PackageId;
            public string Root;
        }

        private sealed class SourceOwnerResolution
        {
            public string PackageId;
            public string AssemblyRelativePath;
            public MethodBase Method;
        }
    }
}
