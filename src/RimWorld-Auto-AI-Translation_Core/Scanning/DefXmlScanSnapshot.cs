using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace AutoTranslator_Core
{
    internal sealed class DefXmlScanMod
    {
        internal string PackageId;
        internal int LoadOrder;
        // Authoritative runtime folders, highest file override priority first.
        internal List<string> ContentRoots;
    }

    internal sealed class DefXmlScanSnapshot
    {
        internal string Identity;
        internal string FileSignature;
        internal DateTime ValidatedUtc;
        internal DefXmlInheritanceContext Context;
        internal DefXmlInheritanceContext BaseContext;
        internal List<string> FailedFiles;
        internal Dictionary<string, DefXmlSourceDocument> Documents;
    }

    internal sealed class DefXmlScanSnapshotCache
    {
        private readonly object sync = new object();
        // Keep only the current configuration and one optional inactive-target view.
        private readonly List<DefXmlScanSnapshot> snapshots = new List<DefXmlScanSnapshot>();
        private readonly Dictionary<string, Tuple<string, XmlDocument>> parsed = new Dictionary<string, Tuple<string, XmlDocument>>(StringComparer.OrdinalIgnoreCase);

        internal void Invalidate()
        {
            lock (sync) { snapshots.Clear(); parsed.Clear(); }
        }

        internal DefXmlScanSnapshot Get(List<DefXmlScanMod> mods, string identity,
            Func<XmlNode, bool> applies, IEnumerable<string> duplicateListFields,
            Func<bool> cancelled, Action<string> warning, DefXmlScanSnapshot baseSnapshot = null)
        {
            lock (sync)
            {
                ThrowIfCancelled(cancelled);
                DefXmlScanSnapshot previous = snapshots.FirstOrDefault(s => s.Identity == identity &&
                    s.BaseContext == baseSnapshot?.Context);
                // Bound metadata I/O during a multi-module scan. Explicit refresh is immediate;
                // external edits/additions/deletions are picked up within two seconds.
                if (previous != null && (DateTime.UtcNow - previous.ValidatedUtc).TotalSeconds < 2) return previous;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var files = SelectFiles(mods);
                    string signature = Signature(files.Select(f => f.Item1));
                    if (previous != null && previous.FileSignature == signature)
                    {
                        previous.ValidatedUtc = DateTime.UtcNow;
                        return previous;
                    }
                    var documents = new Dictionary<string, DefXmlSourceDocument>(StringComparer.OrdinalIgnoreCase);
                    var failures = baseSnapshot == null ? new List<string>() : new List<string>(baseSnapshot.FailedFiles);
                    foreach (var file in files)
                    {
                        ThrowIfCancelled(cancelled);
                        try
                        {
                            string stamp = Signature(new[] { file.Item1 });
                            if (!parsed.TryGetValue(file.Item1, out Tuple<string, XmlDocument> loaded) || loaded.Item1 != stamp)
                            {
                                var document = new XmlDocument { XmlResolver = null };
                                using (XmlReader reader = XmlReader.Create(file.Item1, new XmlReaderSettings {
                                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                                    IgnoreComments = true, IgnoreProcessingInstructions = true })) document.Load(reader);
                                loaded = Tuple.Create(stamp, document);
                                parsed[file.Item1] = loaded;
                            }
                            // Filtering is per snapshot; cached raw documents remain untouched.
                            XmlDocument filtered = (XmlDocument)loaded.Item2.CloneNode(true);
                            Filter(filtered.DocumentElement, applies);
                            documents[file.Item1] = new DefXmlSourceDocument {
                                Document = filtered, SourceFile = file.Item1,
                                PackageId = file.Item2.PackageId, LoadOrder = file.Item2.LoadOrder };
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            failures.Add(file.Item1);
                            warning?.Invoke("Could not process source Def XML " + file.Item1 + ": " + ex.Message);
                        }
                    }
                    ThrowIfCancelled(cancelled);
                    // Never publish a partially built context, or a build that overlapped a
                    // file replacement. Retain no stale snapshot on an unstable source set.
                    if (signature != Signature(SelectFiles(mods).Select(f => f.Item1))) continue;
                    var snapshot = new DefXmlScanSnapshot {
                        Identity = identity, FileSignature = signature, ValidatedUtc = DateTime.UtcNow,
                        Documents = documents, FailedFiles = failures,
                        BaseContext = baseSnapshot?.Context,
                        Context = new DefXmlInheritanceContext(documents.Values, warning, duplicateListFields, baseSnapshot?.Context) };
                    snapshots.RemoveAll(s => s.Identity == identity);
                    snapshots.Add(snapshot);
                    // Retain the shared running stack while replacing inactive overlays.
                    while (snapshots.Count > 2)
                    {
                        int obsolete = snapshots.FindIndex(s => s.BaseContext != null && s != snapshot);
                        snapshots.RemoveAt(obsolete >= 0 ? obsolete : 0);
                    }
                    var retained = new HashSet<string>(snapshots.SelectMany(s => s.Documents.Keys), StringComparer.OrdinalIgnoreCase);
                    foreach (string path in parsed.Keys.Where(p => !retained.Contains(p)).ToList()) parsed.Remove(path);
                    return snapshot;
                }
                throw new IOException("Source Def XML changed repeatedly while building the inheritance context; retry the scan.");
            }
        }

        private static void ThrowIfCancelled(Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
        }

        private static void Filter(XmlNode node, Func<XmlNode, bool> applies)
        {
            if (node == null) return;
            foreach (XmlNode child in node.ChildNodes.Cast<XmlNode>().ToList())
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (!applies(child)) node.RemoveChild(child);
                else Filter(child, applies);
            }
        }

        private static List<Tuple<string, DefXmlScanMod>> SelectFiles(IEnumerable<DefXmlScanMod> mods)
        {
            var files = new List<Tuple<string, DefXmlScanMod>>();
            foreach (DefXmlScanMod mod in mods.OrderBy(m => m.LoadOrder))
            {
                // DirectXmlLoader uses first relative path wins across descending folders;
                // do not include an overridden Common/root file a second time.
                var selected = new HashSet<string>(StringComparer.Ordinal);
                foreach (string root in mod.ContentRoots)
                {
                    string defs = Path.Combine(root, "Defs");
                    if (!Directory.Exists(defs)) continue;
                    foreach (string file in Directory.GetFiles(defs, "*.xml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        if (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal)) continue;
                        string relative = file.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1);
                        if (selected.Add(relative)) files.Add(Tuple.Create(Path.GetFullPath(file), mod));
                    }
                }
            }
            return files;
        }

        private static string Signature(IEnumerable<string> paths)
        {
            var result = new StringBuilder();
            foreach (string path in paths)
            {
                var file = new FileInfo(path);
                result.Append(path).Append('|').Append(file.Exists ? file.Length : -1)
                    .Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
            }
            return result.ToString();
        }
    }
}
