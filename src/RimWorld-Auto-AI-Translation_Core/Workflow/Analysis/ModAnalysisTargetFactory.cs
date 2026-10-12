using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Verse;

namespace AutoTranslator_Core.Workflow.Analysis
{
    public static class ModAnalysisTargetFactory
    {
        public static ModAnalysisTarget Create(
            ModMetaData mod,
            TargetLanguage targetLanguage,
            CancellationToken cancellationToken,
            Action<string, long, long> reportFileProgress = null)
        {
            if (mod?.RootDir == null)
                throw new ArgumentException("Mod metadata is incomplete.", nameof(mod));
            string root = Path.GetFullPath(mod.RootDir.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string modIdentity = CreateModIdentity(mod);
            List<string> effectiveLanguageRoots = AutoTranslatorScanner.GetAllEffectiveLangPaths(mod.PackageId, root);
            List<string> targetTranslationDirectories = effectiveLanguageRoots
                .Concat(AutoTranslatorScanner.GetAllTranslationPatchLangPaths(mod))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(WorkflowPath.Comparer)
                .SelectMany(languageRoot =>
                    AutoTranslatorScanner.GetTargetLanguageBucketPaths(languageRoot, targetLanguage, "Keyed")
                        .Concat(AutoTranslatorScanner.GetTargetLanguageBucketPaths(
                            languageRoot, targetLanguage, "DefInjected")))
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Select(Path.GetFullPath)
                .Distinct(WorkflowPath.Comparer)
                .ToList();
            List<string> xmlDirectories = effectiveLanguageRoots
                .SelectMany(languageRoot =>
                    AutoTranslatorScanner.GetTranslatableLanguageBucketPaths(languageRoot, targetLanguage, "Keyed", false)
                        .Concat(AutoTranslatorScanner.GetTranslatableLanguageBucketPaths(
                            languageRoot, targetLanguage, "DefInjected", false)))
                .Concat(AutoTranslatorScanner.GetAllEffectiveDefsPaths(mod.PackageId, root))
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Select(Path.GetFullPath)
                .Distinct(WorkflowPath.Comparer)
                .ToList();
            List<string> assemblyFiles = AutoTranslatorScanner.GetAllEffectiveAssemblyPaths(
                mod.PackageId,
                root);
            List<string> sourceFiles = EnumerateRelevantFiles(
                root,
                xmlDirectories,
                assemblyFiles,
                cancellationToken);
            List<ModFileRecord> sourceFileRecords = sourceFiles
                .Select(path => CreateFileRecord(
                    modIdentity, root, path, cancellationToken, reportFileProgress))
                .ToList();
            string versionFingerprint = CreateVersionFingerprint(mod.PackageId, sourceFileRecords);
            string snapshotId = "snapshot:" + WorkflowIdentity.HashText(modIdentity + "\n" + versionFingerprint);
            List<string> targetFiles = EnumerateXmlFiles(targetTranslationDirectories, cancellationToken);
            List<ModFileRecord> files = sourceFileRecords
                .Concat(targetFiles.Where(path => !sourceFiles.Contains(path, WorkflowPath.Comparer))
                    .Select(path => CreateFileRecord(
                        modIdentity, root, path, cancellationToken, reportFileProgress)))
                .ToList();
            foreach (ModFileRecord file in files)
            {
                file.ModVersionFingerprint = versionFingerprint;
                file.SnapshotId = snapshotId;
            }

            string workshopId = ReadWorkshopId(root);
            string installationSource = !string.IsNullOrWhiteSpace(workshopId)
                ? "Workshop"
                : (mod.PackageId ?? string.Empty).StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)
                    ? "Official"
                    : "Local";

            return new ModAnalysisTarget
            {
                Mod = mod,
                XmlSourceDirectories = xmlDirectories,
                TargetTranslationDirectories = targetTranslationDirectories,
                TargetLanguage = AutoTranslatorScanner.GetFolderNameByLanguage(targetLanguage),
                Files = files,
                Snapshot = new ModSnapshotRecord
                {
                    SnapshotId = snapshotId,
                    ModIdentity = modIdentity,
                    PackageId = mod.PackageId,
                    NormalizedPackageId = (mod.PackageId ?? string.Empty).Trim().ToLowerInvariant(),
                    DisplayName = mod.Name ?? mod.PackageId,
                    RootPath = root,
                    InstallationSource = installationSource,
                    InstallationSourceId = workshopId,
                    IsActive = mod.Active,
                    GameVersion = ReadGameVersion(),
                    LoadFoldersJson = JsonConvert.SerializeObject(
                        effectiveLanguageRoots.Concat(xmlDirectories)
                            .Select(path => Relative(root, path))
                            .Distinct(WorkflowPath.Comparer)
                            .OrderBy(path => path, WorkflowPath.Comparer)),
                    VersionLabel = ReadVersionLabel(root),
                    VersionFingerprint = versionFingerprint,
                    LastObservedUtc = DateTime.UtcNow
                }
            };
        }

        public static string CreateModIdentity(ModMetaData mod)
        {
            if (mod?.RootDir == null)
                throw new ArgumentException("Mod metadata is incomplete.", nameof(mod));
            string root = Path.GetFullPath(mod.RootDir.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return WorkflowIdentity.CreateModIdentity(
                mod.PackageId, ReadAuthors(mod), mod.Name, Path.GetFileName(root));
        }

        private static string ReadAuthors(ModMetaData mod)
        {
            if (mod == null) return string.Empty;
            try
            {
                PropertyInfo authorsString = typeof(ModMetaData).GetProperty("AuthorsString");
                string direct = authorsString?.GetValue(mod, null) as string;
                if (!string.IsNullOrWhiteSpace(direct)) return direct;
                PropertyInfo authors = typeof(ModMetaData).GetProperty("Authors");
                object value = authors?.GetValue(mod, null);
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<string> EnumerateRelevantFiles(
            string root,
            IEnumerable<string> xmlDirectories,
            IEnumerable<string> assemblyFiles,
            CancellationToken cancellationToken)
        {
            HashSet<string> files = new HashSet<string>(WorkflowPath.Comparer);
            foreach (string directory in xmlDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories))
                    files.Add(Path.GetFullPath(file));
            }
            foreach (string file in assemblyFiles ?? Enumerable.Empty<string>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                    files.Add(Path.GetFullPath(file));
            }
            string about = Path.Combine(root, "About", "About.xml");
            if (File.Exists(about)) files.Add(Path.GetFullPath(about));
            string loadFolders = Path.Combine(root, "LoadFolders.xml");
            if (File.Exists(loadFolders)) files.Add(Path.GetFullPath(loadFolders));
            return files.OrderBy(path => path, WorkflowPath.Comparer).ToList();
        }

        private static List<string> EnumerateXmlFiles(
            IEnumerable<string> directories,
            CancellationToken cancellationToken)
        {
            HashSet<string> files = new HashSet<string>(WorkflowPath.Comparer);
            foreach (string directory in directories ?? Enumerable.Empty<string>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories))
                    files.Add(Path.GetFullPath(file));
            }
            return files.OrderBy(path => path, WorkflowPath.Comparer).ToList();
        }

        private static ModFileRecord CreateFileRecord(
            string modIdentity,
            string root,
            string path,
            CancellationToken cancellationToken,
            Action<string, long, long> reportFileProgress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new FileInfo(path);
            long fileLength = info.Length;
            bool countXmlLines = Path.GetExtension(path).Equals(
                ".xml", StringComparison.OrdinalIgnoreCase);
            int lineCount = 0;
            string fileHash;
            using (FileStream stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] buffer = new byte[65536];
                long completed = 0L;
                long lastReported = -1L;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    algorithm.TransformBlock(buffer, 0, read, buffer, 0);
                    if (countXmlLines)
                        for (int i = 0; i < read; i++)
                            if (buffer[i] == (byte)'\n') lineCount++;
                    completed += read;
                    if (reportFileProgress != null &&
                        (completed >= fileLength || lastReported < 0 || completed - lastReported >= 1048576L))
                    {
                        lastReported = completed;
                        reportFileProgress(path, completed, fileLength);
                    }
                }
                algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                fileHash = ToHex(algorithm.Hash);
            }
            if (countXmlLines && fileLength > 0) lineCount++;
            string relativePath = Relative(root, path);
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(rootPrefix, WorkflowPath.Comparison))
                relativePath = "external/" + Path.GetFileName(path) + "." + fileHash.Substring(0, 12);
            return new ModFileRecord
            {
                ModIdentity = modIdentity,
                RelativePath = relativePath,
                FileHash = fileHash,
                FileLength = fileLength,
                LineCount = countXmlLines ? lineCount : 0,
                EstimatedTokens = 0,
                FileType = GetFileType(relativePath),
                LastWriteUtc = File.GetLastWriteTimeUtc(path),
                IsPresent = true
            };
        }

        private static string CreateVersionFingerprint(string packageId, IEnumerable<ModFileRecord> files)
        {
            StringBuilder material = new StringBuilder(packageId?.Trim().ToLowerInvariant() ?? string.Empty);
            foreach (ModFileRecord file in files)
                material.Append('\n').Append(file.RelativePath).Append('|').Append(file.FileHash);
            return WorkflowIdentity.HashText(material.ToString());
        }

        private static string ReadWorkshopId(string root)
        {
            foreach (string path in new[]
                     {
                         Path.Combine(root, "About", "PublishedFileId.txt"),
                         Path.Combine(root, "PublishedFileId.txt")
                     })
            {
                if (!File.Exists(path)) continue;
                string value = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static string ReadGameVersion()
        {
            try
            {
                Type versionControl = Type.GetType("Verse.VersionControl, Assembly-CSharp", false);
                PropertyInfo property = versionControl?.GetProperty(
                    "CurrentVersionStringWithRev", BindingFlags.Public | BindingFlags.Static);
                return Convert.ToString(property?.GetValue(null, null)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetFileType(string relativePath)
        {
            string normalized = (relativePath ?? string.Empty).Replace('\\', '/');
            if (normalized.EndsWith("/About/About.xml", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("About/About.xml", StringComparison.OrdinalIgnoreCase)) return "About";
            if (normalized.EndsWith("LoadFolders.xml", StringComparison.OrdinalIgnoreCase)) return "LoadFolders";
            string extension = Path.GetExtension(normalized);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return "Assembly";
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return "XML";
            return "Other";
        }

        internal static string ReadVersionLabel(string root)
        {
            try
            {
                string path = Path.Combine(root, "About", "About.xml");
                if (!File.Exists(path)) return string.Empty;
                XDocument document = XDocument.Load(path);
                return document.Root?.Element("modVersion")?.Value?.Trim() ??
                       document.Root?.Element("version")?.Value?.Trim() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, WorkflowPath.Comparison)
                ? path.Substring(prefix.Length).Replace('\\', '/')
                : Path.GetFileName(path);
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes ?? Array.Empty<byte>())
                .Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
