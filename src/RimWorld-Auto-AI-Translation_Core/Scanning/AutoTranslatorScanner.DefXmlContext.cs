using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorScanner
    {
        private static readonly DefXmlScanSnapshotCache RawDefContexts = new DefXmlScanSnapshotCache();

        public static void NotifyRawDefSourcesChanged()
        {
            RawDefContexts.Invalidate();
            InvalidateSelfRepair();
        }

        private static List<string> GetUnavailableRawDefDependencies(ModMetaData target, ISet<string> active)
        {
            return target.Dependencies.Where(d => !active.Contains(d.packageId)
#if !RIMWORLD_1_5
                // Alternative dependencies were added after 1.5. Match the
                // dependency model actually supported by the selected game.
                && !(d.alternativePackageIds ?? new List<string>()).Any(active.Contains)
#endif
                ).Select(d => d.packageId).ToList();
        }

        private static string RunningRawDefIdentity(IEnumerable<ModContentPack> running)
        {
            return string.Join("\n", running.Select(m => m == null ? "<loading>" :
                m.PackageId + "|" + m.PackageIdPlayerFacing + "|" + m.loadOrder + "|" +
                string.Join("|", m.foldersToLoadDescendingOrder ?? new List<string> { "<loading>" })));
        }

        private static List<string> ResolveInactiveRawDefRoots(string modRoot, ISet<string> active)
        {
            string manifest = Path.Combine(modRoot, "LoadFolders.xml");
            if (File.Exists(manifest))
            {
                var document = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(manifest, new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                XmlNode branch = SelectLoadFoldersVersionNode(document.DocumentElement);
                if (branch != null)
                {
                    var roots = new List<string>();
                    foreach (XmlNode entry in branch.ChildNodes.Cast<XmlNode>().Reverse())
                    {
                        if (entry.NodeType != XmlNodeType.Element || entry.Name != "li") continue;
                        Func<string, IEnumerable<string>> ids = attribute => (entry.Attributes?[attribute]?.Value ?? "")
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim());
                        var any = ids("IfModActive").ToList();
                        if (any.Count > 0 && !any.Any(active.Contains)) continue;
                        if (!ids("IfModActiveAll").All(active.Contains)) continue;
                        if (ids("IfModNotActive").Any(active.Contains)) continue;
                        string relative = entry.InnerText.Trim().Replace('/', Path.DirectorySeparatorChar);
                        string path = relative == "\\" || relative == "/" || relative.Length == 0
                            ? modRoot : Path.Combine(modRoot, relative);
                        if (Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
                    }
                    return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            var fallback = new List<string>();
            string version = FindBestFallbackVersionRoot(modRoot);
            if (version != null) fallback.Add(version);
            string common = Path.Combine(modRoot, "Common");
            if (Directory.Exists(common)) fallback.Add(common);
            if (Directory.Exists(modRoot)) fallback.Add(modRoot);
            return fallback.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<ResolvedDefXmlNode> ResolveRawDefTargets(string defsRoot, out List<string> failedFiles)
        {
            failedFiles = new List<string>();
            string targetRoot = Path.GetFullPath(defsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ModContentPack[] running = LoadedModManager.RunningModsListForReading.ToArray();
            string runningIdentity = RunningRawDefIdentity(running);
            if (running.Length == 0 || running.Any(m => m == null || m.foldersToLoadDescendingOrder == null))
                throw new InvalidOperationException("Running mod load folders are not ready; retry after loading completes.");
            var mods = running.Where(m => m != null).Select(m => new DefXmlScanMod {
                PackageId = m.PackageId, LoadOrder = m.loadOrder,
                ContentRoots = (m.foldersToLoadDescendingOrder ?? new List<string>()).Select(Path.GetFullPath).ToList()
            }).OrderBy(m => m.LoadOrder).ToList();
            var active = new HashSet<string>(running.Where(m => m != null).SelectMany(m => new[] {
                m.PackageId, m.PackageIdPlayerFacing }), StringComparer.OrdinalIgnoreCase);
            bool activeTarget = mods.Any(m => m.ContentRoots.Any(r => PathsEqual(Path.Combine(r, "Defs"), targetRoot)));
            DefXmlScanMod inactiveTarget = null;
            if (!activeTarget)
            {
                // An inactive target is an explicit offline view against the running stack.
                // Never load its disabled dependencies implicitly. Reject an incomplete
                // dependency closure rather than inventing an order/activation state.
                ModMetaData target = ModLister.AllInstalledMods.FirstOrDefault(m => m.RootDir != null &&
                    (PathsEqual(m.RootDir.FullName, Path.GetDirectoryName(targetRoot)) ||
                     targetRoot.StartsWith(m.RootDir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
                if (target == null) throw new InvalidOperationException("Cannot determine the owning mod for source Defs: " + targetRoot);
                var unavailable = GetUnavailableRawDefDependencies(target, active);
                if (unavailable.Count > 0) throw new InvalidOperationException("Inactive source mod " + target.PackageId +
                    " requires dependencies in the running stack: " + string.Join(", ", unavailable) + ". Enable the target and dependencies, then restart before scanning.");
                List<string> roots = ResolveInactiveRawDefRoots(target.RootDir.FullName, active);
                if (!roots.Any(r => PathsEqual(Path.Combine(r, "Defs"), targetRoot)))
                    throw new InvalidOperationException("Source Defs folder is not applicable to the current version/LoadFolders: " + targetRoot);
                inactiveTarget = new DefXmlScanMod { PackageId = target.PackageId,
                    LoadOrder = mods.Count == 0 ? 0 : mods.Max(m => m.LoadOrder) + 1, ContentRoots = roots };
            }

            string identity = CurrentRimWorldVersion + "\n" + string.Join("\n", mods.Select(m =>
                m.PackageId + "|" + m.LoadOrder + "|" + string.Join("|", m.ContentRoots))) +
                "\nactive=" + string.Join("|", active.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            Func<XmlNode, bool> applies = node => {
                string all = node.Attributes?["MayRequire"]?.Value;
                if (all != null && !all.Split(',').All(id => active.Contains(id.Trim()))) return false;
                string any = node.Attributes?["MayRequireAnyOf"]?.Value;
                return any == null || any.Split(',').Any(id => active.Contains(id.Trim()));
            };
            DefXmlScanSnapshot snapshot = RawDefContexts.Get(mods, identity, applies,
                XmlInheritance.allowDuplicateNodesFieldNames, () => AutoTranslatorSettings.IsCancellationRequested,
                message => Log.Warning("[AutoTranslationCore] " + message));
            if (inactiveTarget != null)
            {
                string overlayIdentity = identity + "\ninactive=" + inactiveTarget.PackageId + "|" + inactiveTarget.LoadOrder + "|" + string.Join("|", inactiveTarget.ContentRoots);
                snapshot = RawDefContexts.Get(new List<DefXmlScanMod> { inactiveTarget }, overlayIdentity, applies,
                    XmlInheritance.allowDuplicateNodesFieldNames, () => AutoTranslatorSettings.IsCancellationRequested,
                    message => Log.Warning("[AutoTranslationCore] " + message), snapshot);
            }
            if (runningIdentity != RunningRawDefIdentity(LoadedModManager.RunningModsListForReading.ToArray()))
                throw new InvalidOperationException("Running mods or load folders changed while building the source inheritance context; retry the scan.");
            failedFiles = new List<string>(snapshot.FailedFiles);
            string prefix = targetRoot + Path.DirectorySeparatorChar;
            var resolved = snapshot.Context.ResolveTargets(snapshot.Documents.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            failedFiles.AddRange(resolved.Where(n => n.InheritanceIncomplete).Select(n => n.SourceFile));
            failedFiles = failedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return resolved;
        }
    }
}
