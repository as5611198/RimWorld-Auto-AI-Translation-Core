using AutoTranslator_Core.Workflow.Analysis;
using AutoTranslator_Core.Workflow.Output;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Verse;

namespace AutoTranslator_Core.Workflow
{
    public sealed class HistoryMigrationResult
    {
        public string TargetLanguage { get; set; }
        public int CompletedMods { get; set; }
        public int Imported { get; set; }
        public int Existing { get; set; }
        public int Unmatched { get; set; }
        public int Invalid { get; set; }
        public List<string> Diagnostics { get; set; } = new List<string>();
        // Persisted in WorkflowRuns.result_json: the old XML does not contain original hashes.
        public List<string> ImportedWithoutHistoricalOriginal { get; set; } = new List<string>();
        public override string ToString()
        {
            return "历史数据迁移：" + TargetLanguage + "\n已完成 Mod：" + CompletedMods +
                "\n导入：" + Imported + "；已有译文跳过：" + Existing +
                "\n未匹配：" + Unmatched + "；格式不合格：" + Invalid +
                "\n全部导入译文记为 AI 来源，未核对历史原文。\n" +
                string.Join("\n", Diagnostics.Take(20)) +
                "\n旧文件保持不变；需要生效时请点击热重载。";
        }
    }

    public sealed partial class WorkflowBackend
    {
        public Task<HistoryMigrationResult> RunHistoricalDataMigrationAsync(
            IList<ModMetaData> mods, CancellationToken cancellationToken = default(CancellationToken))
        {
            var targets = WorkflowStepSelection.FilterTranslationTargets(mods);
            string language = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            return RunExclusiveAsync(WorkflowTaskKind.HistoricalDataMigration,
                "迁移历史数据", token => MigrateHistoricalXmlAsync(targets, language, token),
                cancellationToken, JsonConvert.SerializeObject(new { historicalMigration = true, language }));
        }

        private async Task<HistoryMigrationResult> MigrateHistoricalXmlAsync(
            IList<ModMetaData> mods, string language, CancellationToken token)
        {
            var result = new HistoryMigrationResult { TargetLanguage = language };
            string reportKey = "history-migration-report:" + Guid.NewGuid().ToString("N");
            string languageRoot = Path.Combine(_generatedPackRoot, "Languages", language);
            // No recursive pre-count or percentage/ETA. Discover files only for the Mod being handled.
            var owners = mods.Where(mod => !string.IsNullOrWhiteSpace(mod.PackageId))
                .GroupBy(mod => mod.PackageId.Replace('.', '_'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var owner in owners.OrderByDescending(pair => pair.Key.Length))
            {
                token.ThrowIfCancellationRequested();
                if (owner.Value.Count != 1)
                {
                    result.Diagnostics.Add("包名前缀冲突，保留未迁移：" + owner.Key);
                    continue;
                }
                ModMetaData mod = owner.Value[0];
                string modIdentity = ModAnalysisTargetFactory.CreateModIdentity(mod);
                WorkflowTaskCoordinator.Instance.ReportStage(modIdentity, "迁移历史数据",
                    "正在处理 " + mod.Name + "；已完成 " + result.CompletedMods + " 个 Mod",
                    "workflow.history mod=" + modIdentity);
                try
                {
                    var files = FindHistoricalXmlFiles(languageRoot, owner.Key, owners.Keys).ToList();
                    if (files.Count == 0) continue;
                    // Read historical files before analysis. Analysis may discover higher-priority native text.
                    var documents = new List<KeyValuePair<string, XDocument>>();
                    foreach (string file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        try { documents.Add(new KeyValuePair<string, XDocument>(file, XDocument.Load(file))); }
                        catch (Exception ex) { result.Diagnostics.Add(file + ": " + ex.Message); }
                    }
                    await _analysis.RunXmlOnlyAsync(new List<ModMetaData> { mod }, false, token);
                    var candidates = _repository.GetCandidates(new[] { modIdentity }, language)
                        .Where(candidate => candidate.SourceDomain == CandidateSourceDomain.Xml).ToList();
                    foreach (var document in documents)
                    {
                        if (document.Value.Root == null || document.Value.Root.Name.LocalName != "LanguageData")
                        {
                            result.Diagnostics.Add("不是 LanguageData 译文文件：" + document.Key);
                            continue;
                        }
                        var duplicateKeys = new HashSet<string>(document.Value.Root.Elements()
                            .GroupBy(element => element.Name.LocalName)
                            .Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.Ordinal);
                        foreach (XElement entry in document.Value.Root.Elements())
                        {
                            token.ThrowIfCancellationRequested();
                            if (entry.HasElements || duplicateKeys.Contains(entry.Name.LocalName))
                            { result.Unmatched++; continue; }
                            var matches = candidates.Where(item =>
                                item.TranslationEntryKey == entry.Name.LocalName &&
                                MatchesHistoricalFile(item, document.Key, language, owner.Key)).ToList();
                            if (matches.Count != 1) { result.Unmatched++; continue; }
                            CandidateRecord candidate = matches[0];
                            // Conservative fill-only: even a failed/stale record with text remains untouched.
                            if (!string.IsNullOrWhiteSpace(candidate.TranslationText))
                            { result.Existing++; continue; }
                            if (!AutoTranslatorScanner.TryAcceptTranslatedValue(entry.Value, candidate.SourceText,
                                out string text, out string reason, out string detail))
                            {
                                result.Invalid++;
                                result.Diagnostics.Add(candidate.CandidateId + ": " + reason + " " + detail);
                                continue;
                            }
                            string relative = document.Key.Substring(_generatedPackRoot.Length)
                                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                            if (!_repository.TrySaveHistoricalTranslation(candidate.CandidateId, language,
                                text, relative, entry.Name.LocalName))
                            { result.Existing++; continue; }
                            candidate.TranslationText = text;
                            result.Imported++;
                            result.ImportedWithoutHistoricalOriginal.Add(candidate.CandidateId);
                        }
                    }
                    result.CompletedMods++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result.Diagnostics.Add(mod.Name + ": " + ex.Message); }
                finally
                {
                    // Retain imported-ID evidence even when the user cancels partway through a Mod.
                    _repository.SaveWorkflowSettingJson(reportKey, JsonConvert.SerializeObject(result));
                    WorkflowTaskCoordinator.Instance.ReportStage(modIdentity, "迁移历史数据",
                        "已完成 " + result.CompletedMods + " 个 Mod；已导入 " + result.Imported + " 条",
                        "workflow.history imported=" + result.Imported + " existing=" + result.Existing);
                }
            }
            if (File.Exists(Path.Combine(_generatedPackRoot, "UI_Hardcoded_Cache.json")))
                result.Diagnostics.Add("旧 UI 缓存缺少 Mod/调用位置，本次不导入 DLL 条目，原文件保留。");
            AutoTranslatorSettings.AddLog(result.ToString());
            _repository.SaveWorkflowSettingJson(reportKey, JsonConvert.SerializeObject(result));
            return result;
        }

        private static IEnumerable<string> FindHistoricalXmlFiles(
            string root, string prefix, IEnumerable<string> knownPrefixes)
        {
            foreach (string bucket in new[] { "Keyed", "DefInjected" })
            {
                string directory = Path.Combine(root, bucket);
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, prefix + "_*.xml", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    // A longer package prefix owns its own files; never assign them to a shorter Mod ID.
                    if (knownPrefixes.Any(other => other.Length > prefix.Length &&
                        name.StartsWith(other + "_", StringComparison.OrdinalIgnoreCase))) continue;
                    yield return file;
                }
            }
        }

        private bool MatchesHistoricalFile(CandidateRecord candidate, string file, string language, string prefix)
        {
            string relative = file.Substring(_generatedPackRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            TranslationWriteTarget target = _output.Resolve(candidate, language);
            if (string.Equals(relative, target.RelativePath, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(candidate.EntryKind, "Keyed", StringComparison.OrdinalIgnoreCase)) return false;
            string sourceName = Path.GetFileName((candidate.SourceFileRelativePath ?? "").Replace('/', Path.DirectorySeparatorChar));
            return string.Equals(relative, "Languages/" + language + "/Keyed/" + prefix + "_" + sourceName,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
