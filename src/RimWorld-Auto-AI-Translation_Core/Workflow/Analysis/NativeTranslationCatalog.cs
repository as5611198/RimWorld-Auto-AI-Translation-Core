using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator_Core.TranslationPolicy;
using AutoTranslator_Core.Workflow.Persistence;
using Verse;

namespace AutoTranslator_Core.Workflow.Analysis
{
    public sealed class ModCatalogSummary
    {
        public int Installed;
        public int Enabled;
        public int NewlyEnabled;
        public int Disabled;
        public int Added;
        public int Removed;
        public bool IsInitial;
        public bool Unchanged;
    }

    internal sealed class NativeTranslationEntry
    {
        public string ModIdentity = string.Empty;
        public string PackageId = string.Empty;
        public string SourceFile = string.Empty;
        public string Bucket = string.Empty;
        public string DefType = string.Empty;
        public string EntryKey = string.Empty;
        public string Text = string.Empty;
    }

    internal sealed class NativeTranslationCatalog
    {
        private readonly WorkflowRepository _repository;
        public NativeTranslationCatalog(WorkflowRepository repository) { _repository = repository; }

        internal static Task<List<ModMetaData>> CaptureInstalledModsAsync()
        {
            var completion = new TaskCompletionSource<List<ModMetaData>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                try
                {
                    completion.TrySetResult(ModLister.AllInstalledMods
                        .Where(mod => mod?.RootDir != null).ToList());
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task;
        }

        internal static ModSnapshotRecord Describe(ModMetaData mod)
        {
            return new ModSnapshotRecord
            {
                ModIdentity = ModAnalysisTargetFactory.CreateModIdentity(mod),
                PackageId = mod.PackageId ?? string.Empty,
                NormalizedPackageId = (mod.PackageId ?? string.Empty).Trim().ToLowerInvariant(),
                DisplayName = mod.Name ?? string.Empty,
                RootPath = mod.RootDir.FullName,
                IsActive = mod.Active,
                VersionLabel = ModAnalysisTargetFactory.ReadVersionLabel(mod.RootDir.FullName),
                LastObservedUtc = DateTime.UtcNow
            };
        }

        internal async Task RefreshAsync(CancellationToken token)
        {
            List<ModMetaData> installed = await CaptureInstalledModsAsync();
            // Use the actual running set, not settings that may have been edited for next restart.
            var loadedCompletion = new TaskCompletionSource<List<ModMetaData>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                try { loadedCompletion.TrySetResult(LoadedModManager.RunningModsListForReading
                    .Select(pack => installed.FirstOrDefault(mod => WorkflowPath.Comparer.Equals(
                        mod.RootDir.FullName, pack.RootDir)))
                    .Select(mod => mod ?? throw new InvalidOperationException("已加载 Mod 的安装信息无法对应，未继续补译。"))
                    .ToList()); }
                catch (Exception ex) { loadedCompletion.TrySetException(ex); }
            });
            List<ModMetaData> loaded = await loadedCompletion.Task;
            await Task.Run(() =>
            {
                _repository.SetLoadedNativeTranslationSources(new List<string>());
                _repository.SynchronizeModCatalog(installed.Select(Describe).ToList(), token);
                string language = WorkflowRuntimeSettings.GetTargetLanguageFolder();
                TargetLanguage languageOption = WorkflowRuntimeSettings.GetTargetLanguage();
                string generatedRoot = AutoTranslatorScanner.GetLocalPackPath().TrimEnd('\\', '/');
                loaded = loaded.Where(mod => !WorkflowPath.Comparer.Equals(
                    mod.RootDir.FullName.TrimEnd('\\', '/'), generatedRoot)).ToList();
                int completed = 0;
                int reused = 0;
                int failed = 0;
                foreach (ModMetaData mod in loaded)
                {
                    token.ThrowIfCancellationRequested();
                    ModSnapshotRecord source = Describe(mod);
                    WorkflowTaskCoordinator.Instance.ReportStage(source.PackageId,
                        "读取原生译文 " + (completed + 1) + "/" + loaded.Count,
                        string.Empty, string.Empty);
                    string currentFile = string.Empty;
                    try
                    {
                        List<string> roots = AutoTranslatorScanner.GetAllEffectiveLangPaths(mod)
                            .Concat(AutoTranslatorScanner.GetAllTranslationPatchLangPaths(mod))
                            .Distinct(WorkflowPath.Comparer).ToList();
                        List<string> directories = roots.SelectMany(root =>
                            AutoTranslatorScanner.GetTargetLanguageBucketPaths(root, languageOption, "Keyed")
                                .Concat(AutoTranslatorScanner.GetTargetLanguageBucketPaths(root, languageOption, "DefInjected")))
                            .Distinct(WorkflowPath.Comparer).ToList();
                        List<string> files = directories.Where(Directory.Exists)
                            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
                            .Select(Path.GetFullPath).Distinct(WorkflowPath.Comparer)
                            .OrderBy(path => path, WorkflowPath.Comparer).ToList();
                        // Include source file stamps: authors frequently do not change their version label.
                        List<string> versionFiles = AutoTranslatorScanner.GetAllEffectiveDefsPaths(mod)
                            .Where(Directory.Exists)
                            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
                            .Concat(AutoTranslatorScanner.GetAllEffectiveAssemblyPaths(mod.PackageId, source.RootPath))
                            .Concat(files)
                            .Concat(new[] { Path.Combine(source.RootPath, "About", "About.xml"),
                                Path.Combine(source.RootPath, "LoadFolders.xml") })
                            .Where(File.Exists).Distinct(WorkflowPath.Comparer)
                            .OrderBy(path => path, WorkflowPath.Comparer).ToList();
                        string fingerprint = WorkflowIdentity.HashText("native-1\n" + source.RootPath + "\n" +
                            source.VersionLabel + "\n" + language + "\n" + string.Join("\n", versionFiles.Select(path =>
                            {
                                token.ThrowIfCancellationRequested();
                                FileInfo info = new FileInfo(path);
                                return path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
                            })));
                        if (_repository.HasNativeTranslationScan(source.ModIdentity, language, fingerprint))
                            reused++;
                        else
                        {
                            var entries = new List<NativeTranslationEntry>();
                            var fileErrors = new List<string>();
                            foreach (string file in files)
                            {
                                token.ThrowIfCancellationRequested();
                                currentFile = file;
                                try
                                {
                                string normalized = file.Replace('\\', '/');
                                int marker = normalized.LastIndexOf("/DefInjected/", StringComparison.OrdinalIgnoreCase);
                                string defType = marker >= 0
                                    ? normalized.Substring(marker + 13).Split('/')[0] : string.Empty;
                                string xml = File.ReadAllText(file);
                                // Some Mods ship empty placeholder files in language folders.
                                // They contain no translations and must not abort the scan.
                                if (string.IsNullOrWhiteSpace(xml)) continue;
                                List<TranslationPolicyCandidate> parsed = marker >= 0
                                    ? TranslationPolicyXmlScanner.ScanDefInjectedXml(xml, defType, new TranslationPolicySourceContext())
                                    : TranslationPolicyXmlScanner.ScanKeyedXml(xml, new TranslationPolicySourceContext());
                                foreach (TranslationPolicyCandidate entry in parsed)
                                {
                                    if (string.IsNullOrWhiteSpace(entry.SourceText)) continue;
                                    entries.Add(new NativeTranslationEntry
                                    {
                                        ModIdentity = source.ModIdentity, PackageId = source.PackageId,
                                        SourceFile = file, Bucket = entry.Bucket.ToString(), DefType = entry.DefType ?? string.Empty,
                                        EntryKey = entry.KeyOrPath, Text = entry.SourceText
                                    });
                                }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    string error = file + "：" + ex.Message;
                                    fileErrors.Add(error);
                                    AutoTranslatorSettings.AddWarningLog(
                                        "原生译文文件读取失败，继续读取其他文件：" + source.DisplayName + "；" + error);
                                }
                            }
                            token.ThrowIfCancellationRequested();
                            currentFile = string.Empty;
                            _repository.ReplaceNativeTranslationScan(source.ModIdentity, language, fingerprint, entries,
                                string.Join("\n", fileErrors));
                            if (fileErrors.Count > 0) failed++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _repository.FailNativeTranslationScan(source.ModIdentity, language, ex.Message);
                        failed++;
                        AutoTranslatorSettings.AddWarningLog(
                            "原生译文读取失败，继续分析其他内容：" + source.DisplayName +
                            (string.IsNullOrEmpty(currentFile) ? string.Empty : "；文件=" + currentFile) +
                            "；原因=" + ex.Message);
                    }
                    completed++;
                    WorkflowTaskCoordinator.Instance.ReportProgress(completed, loaded.Count,
                        source.PackageId, "原生译文扫描 " + completed + "/" + loaded.Count + "，复用 " + reused,
                        writeRuntimeLog: false);
                }
                token.ThrowIfCancellationRequested();
                if (!string.Equals(language, WorkflowRuntimeSettings.GetTargetLanguageFolder(), StringComparison.Ordinal))
                    throw new InvalidOperationException("扫描期间目标语种发生变化，请按当前语种重新分析。");
                _repository.SetLoadedNativeTranslationSources(loaded.Select(ModAnalysisTargetFactory.CreateModIdentity).ToList());
                AutoTranslatorSettings.AddLog("原生译文收集完成：" + loaded.Count + " 个已加载 Mod，复用 " + reused +
                    " 个，读取不完整 " + failed + " 个。" + (failed > 0 ? "成功文件参与匹配，读取失败范围保留已有译文保护，继续后续分析。" : string.Empty));
            }, token);
        }
    }
}
