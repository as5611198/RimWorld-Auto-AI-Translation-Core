using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using AutoTranslator_Core.Workflow.AI;
using AutoTranslator_Core.Workflow.Synchronization;
using Newtonsoft.Json;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal sealed partial class WorkflowRepository
    {
        private const string SelectedWorkflowModsTable = "SelectedWorkflowMods";
        private const string SelectedWorkflowAnalysisModsTable = "SelectedWorkflowAnalysisMods";
        private const string SelectedWorkflowPackagesTable = "SelectedWorkflowPackages";
        private const string SelectedWorkflowCandidatesTable = "SelectedWorkflowCandidates";
        private const string SelectedProtectedCheckTable = "SelectedProtectedCheck";
        private const string CurrentExternalTranslationsTable = "CurrentExternalTranslations";
        private const string CurrentLoadedNativeSourcesTable = "CurrentLoadedNativeSources";
        private const string ChangedTranslationFilesTable = "ChangedTranslationFiles";
        private const string ModIdentityColumn = "mod_identity";
        private const string PackageIdColumn = "package_id";
        private const string CandidateIdColumn = "candidate_id";
        private const string RelativePathColumn = "relative_path";
        // Candidates retain the most recently written local analysis for each domain.
        // Historical success in another language does not describe those current rows.
        private const string CurrentAnalysisRunSql = @"runs.target_language=@analysis_language
            AND runs.rowid=(SELECT MAX(latest.rowid) FROM AnalysisRuns latest
                WHERE latest.mod_identity=runs.mod_identity
                  AND latest.source_domain=runs.source_domain AND latest.state=1)";
        private readonly WorkflowDatabaseConnectionFactory _connections;

        public WorkflowRepository(WorkflowDatabaseConnectionFactory connections)
        {
            _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        }

        public string DatabasePath => _connections.DatabasePath;

        public ExpiredWorkflowDataSummary GetExpiredDataSummary()
        {
            using (DbConnection connection = _connections.OpenConnection())
            {
                return ReadExpiredDataSummary(connection);
            }
        }

        public ExpiredWorkflowDataSummary CleanupExpiredData(
            CancellationToken cancellationToken,
            Action<int, int, string> reportProgress)
        {
            const int totalStages = 4;
            reportProgress?.Invoke(0, totalStages, "统计过期数据");
            cancellationToken.ThrowIfCancellationRequested();

            ExpiredWorkflowDataSummary result;
            using (DbConnection connection = _connections.OpenConnection())
            {
                result = ReadExpiredDataSummary(connection);
                if (!result.HasExpiredData)
                {
                    reportProgress?.Invoke(totalStages, totalStages, "没有需要清理的过期数据");
                    result.DatabaseBytesAfter = result.DatabaseBytesBefore;
                    return result;
                }

                using (DbTransaction transaction = connection.BeginTransaction())
                {
                    reportProgress?.Invoke(1, totalStages, "删除过期候选的关联状态");
                    cancellationToken.ThrowIfCancellationRequested();
                    Execute(connection, transaction,
                        @"DELETE FROM TranslationFileEntries
                          WHERE candidate_id IN (SELECT candidate_id FROM Candidates WHERE is_present=0);");
                    Execute(connection, transaction,
                        @"DELETE FROM PendingFileOperations
                          WHERE candidate_id IN (SELECT candidate_id FROM Candidates WHERE is_present=0);");

                    reportProgress?.Invoke(2, totalStages, "删除过期候选和关联译文记录");
                    cancellationToken.ThrowIfCancellationRequested();
                    Execute(connection, transaction, "DELETE FROM Candidates WHERE is_present=0;");
                    transaction.Commit();
                }
            }

            reportProgress?.Invoke(3, totalStages, "压缩数据库并回收磁盘空间");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (DbConnection connection = _connections.OpenConnection())
                {
                    Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                    Execute(connection, null, "VACUUM;");
                    Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                }
                result.DatabaseCompacted = true;
            }
            catch (Exception ex)
            {
                result.DatabaseCompacted = false;
                AutoTranslatorSettings.AddErrorLog(
                    "过期数据已删除，但数据库压缩失败：" + ex.GetBaseException().Message);
                AutoTranslatorSettings.AddDebugLog(
                    "workflow.expired-data-cleanup vacuum failed exception=" + ex);
            }

            result.DatabaseBytesAfter = GetDatabaseFileLength();
            reportProgress?.Invoke(totalStages, totalStages, result.DatabaseCompacted
                ? "过期数据清理及数据库压缩完成"
                : "过期数据已清理，数据库压缩未完成");
            return result;
        }

        public void Initialize()
        {
            AutoTranslatorSettings.AddLog(
                "⏳ 工作流数据库初始化／结构升级检查开始（目标版本 " +
                WorkflowDatabaseSchema.CurrentVersion + "）");
            AutoTranslatorSettings.AddDebugLog("workflow.database initialize path=" + DatabasePath);
            try
            {
                using (DbConnection connection = _connections.OpenConnection())
                {
                    WorkflowDatabaseSchema.EnsureCurrent(connection);
                    InitializeReferenceDictionary(connection);
                    InvalidateObsoleteAnalyzerCandidates(connection);
                }
                AutoTranslatorSettings.AddDebugLog("workflow.database ready schema=" + WorkflowDatabaseSchema.CurrentVersion);
                AutoTranslatorSettings.AddLog(
                    "✓ 工作流数据库初始化完成（结构版本 " +
                    WorkflowDatabaseSchema.CurrentVersion + "）");
            }
            catch (Exception ex) when (IsConfirmedCorruption(ex))
            {
                _connections.DeleteDatabaseFilesAfterConfirmedCorruption();
                Verse.Log.Error("[AutoTranslationCore] Workflow database was corrupt and has been rebuilt: " + ex.Message);
                using (DbConnection connection = _connections.OpenConnection())
                {
                    WorkflowDatabaseSchema.EnsureCurrent(connection);
                    InitializeReferenceDictionary(connection);
                }
            }
        }

        public void UpsertMod(ModSnapshotRecord mod)
        {
            if (mod == null) throw new ArgumentNullException(nameof(mod));
            if (string.IsNullOrWhiteSpace(mod.SnapshotId))
                mod.SnapshotId = "snapshot:" + WorkflowIdentity.HashText(
                    mod.ModIdentity + "\n" + mod.VersionFingerprint);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT INTO Mods
                        (mod_identity, package_id, normalized_package_id, display_name, root_path,
                         installation_source, installation_source_id, is_active,
                         version_label, version_fingerprint, last_observed_utc)
                        VALUES (@id, @package, @normalized_package, @name, @root,
                                @source, @source_id, @active, @label, @fingerprint, @observed)
                        ON CONFLICT(mod_identity) DO UPDATE SET
                        package_id=excluded.package_id,
                        normalized_package_id=excluded.normalized_package_id,
                        display_name=excluded.display_name,
                        root_path=excluded.root_path,
                        installation_source=excluded.installation_source,
                        installation_source_id=excluded.installation_source_id,
                        is_active=excluded.is_active,
                        version_label=excluded.version_label,
                        version_fingerprint=excluded.version_fingerprint,
                        last_observed_utc=excluded.last_observed_utc;";
                    Add(command, "@id", mod.ModIdentity);
                    Add(command, "@package", mod.PackageId);
                    Add(command, "@normalized_package", mod.NormalizedPackageId);
                    Add(command, "@name", mod.DisplayName);
                    Add(command, "@root", mod.RootPath);
                    Add(command, "@source", mod.InstallationSource);
                    Add(command, "@source_id", mod.InstallationSourceId);
                    Add(command, "@active", mod.IsActive ? 1 : 0);
                    Add(command, "@label", mod.VersionLabel);
                    Add(command, "@fingerprint", mod.VersionFingerprint);
                    Add(command, "@observed", ToDbTime(mod.LastObservedUtc));
                    command.ExecuteNonQuery();
                }
                using (DbCommand retireOldCandidates = connection.CreateCommand())
                {
                    retireOldCandidates.Transaction = transaction;
                    retireOldCandidates.CommandText = @"UPDATE Candidates SET
                        is_present=0, updated_utc=@updated
                        WHERE mod_identity=@mod AND is_present=1
                          AND mod_version_fingerprint<>@fingerprint;";
                    Add(retireOldCandidates, "@updated", ToDbTime(DateTime.UtcNow));
                    Add(retireOldCandidates, "@mod", mod.ModIdentity);
                    Add(retireOldCandidates, "@fingerprint", mod.VersionFingerprint);
                    retireOldCandidates.ExecuteNonQuery();
                }
                using (DbCommand clearCurrent = connection.CreateCommand())
                {
                    clearCurrent.Transaction = transaction;
                    clearCurrent.CommandText = @"UPDATE ModVersionSnapshots SET is_current=0
                        WHERE mod_identity=@mod AND snapshot_id<>@snapshot;";
                    Add(clearCurrent, "@mod", mod.ModIdentity);
                    Add(clearCurrent, "@snapshot", mod.SnapshotId);
                    clearCurrent.ExecuteNonQuery();
                }
                using (DbCommand clearActiveRoot = connection.CreateCommand())
                {
                    clearActiveRoot.Transaction = transaction;
                    clearActiveRoot.CommandText = @"UPDATE ModInstallations SET is_active=0
                        WHERE mod_identity=@mod AND root_path<>@root;";
                    Add(clearActiveRoot, "@mod", mod.ModIdentity);
                    Add(clearActiveRoot, "@root", mod.RootPath);
                    clearActiveRoot.ExecuteNonQuery();
                }
                using (DbCommand installation = connection.CreateCommand())
                {
                    installation.Transaction = transaction;
                    installation.CommandText = @"INSERT INTO ModInstallations
                        (mod_identity, root_path, installation_source, installation_source_id,
                         is_active, last_observed_utc)
                        VALUES (@mod, @root, @source, @source_id, @active, @observed)
                        ON CONFLICT(mod_identity, root_path) DO UPDATE SET
                        installation_source=excluded.installation_source,
                        installation_source_id=excluded.installation_source_id,
                        is_active=excluded.is_active,
                        last_observed_utc=excluded.last_observed_utc;";
                    Add(installation, "@mod", mod.ModIdentity);
                    Add(installation, "@root", mod.RootPath);
                    Add(installation, "@source", mod.InstallationSource);
                    Add(installation, "@source_id", mod.InstallationSourceId);
                    Add(installation, "@active", mod.IsActive ? 1 : 0);
                    Add(installation, "@observed", ToDbTime(mod.LastObservedUtc));
                    installation.ExecuteNonQuery();
                }
                using (DbCommand snapshot = connection.CreateCommand())
                {
                    snapshot.Transaction = transaction;
                    snapshot.CommandText = @"INSERT INTO ModVersionSnapshots
                        (snapshot_id, mod_identity, mod_version, game_version, load_folders_json,
                         content_fingerprint, created_utc, is_current)
                        VALUES (@snapshot, @mod, @version, @game, @folders, @fingerprint, @created, 1)
                        ON CONFLICT(snapshot_id) DO UPDATE SET
                        mod_version=excluded.mod_version,
                        game_version=excluded.game_version,
                        load_folders_json=excluded.load_folders_json,
                        content_fingerprint=excluded.content_fingerprint,
                        is_current=1;";
                    Add(snapshot, "@snapshot", mod.SnapshotId);
                    Add(snapshot, "@mod", mod.ModIdentity);
                    Add(snapshot, "@version", mod.VersionLabel);
                    Add(snapshot, "@game", mod.GameVersion);
                    Add(snapshot, "@folders", mod.LoadFoldersJson);
                    Add(snapshot, "@fingerprint", mod.VersionFingerprint);
                    Add(snapshot, "@created", ToDbTime(mod.LastObservedUtc));
                    snapshot.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void ReplaceModFiles(
            string modIdentity,
            string modVersionFingerprint,
            IList<ModFileRecord> files)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                using (DbCommand delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = @"DELETE FROM ModFiles
                        WHERE mod_identity=@mod AND mod_version_fingerprint=@version;";
                    Add(delete, "@mod", modIdentity);
                    Add(delete, "@version", modVersionFingerprint);
                    delete.ExecuteNonQuery();
                }
                foreach (ModFileRecord file in files ?? Array.Empty<ModFileRecord>())
                {
                    using (DbCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"INSERT INTO ModFiles
                            (mod_identity, mod_version_fingerprint, relative_path, file_hash,
                             file_length, line_count, estimated_tokens, snapshot_id,
                             file_type, last_write_utc, is_present)
                            VALUES (@mod, @version, @path, @hash, @length, @lines, @tokens,
                                    @snapshot, @type, @last_write, @present);";
                        Add(insert, "@mod", modIdentity);
                        Add(insert, "@version", modVersionFingerprint);
                        Add(insert, "@path", file.RelativePath);
                        Add(insert, "@hash", file.FileHash);
                        Add(insert, "@length", file.FileLength);
                        Add(insert, "@lines", file.LineCount);
                        Add(insert, "@tokens", file.EstimatedTokens);
                        Add(insert, "@snapshot", file.SnapshotId);
                        Add(insert, "@type", file.FileType);
                        Add(insert, "@last_write", ToDbTime(file.LastWriteUtc));
                        Add(insert, "@present", file.IsPresent ? 1 : 0);
                        insert.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        public bool HasSuccessfulAnalysis(
            string modIdentity,
            string modVersionFingerprint,
            CandidateSourceDomain domain,
            string analysisFingerprint)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT 1 FROM (
                    SELECT * FROM AnalysisRuns WHERE mod_identity=@mod AND source_domain=@domain
                      AND state=@completed ORDER BY rowid DESC LIMIT 1)
                    WHERE mod_identity=@mod AND mod_version_fingerprint=@mod_version
                      AND source_domain=@domain AND analysis_fingerprint=@fingerprint AND state=@completed
                      AND target_language=@analysis_language
                    LIMIT 1;";
                Add(command, "@analysis_language", WorkflowRuntimeSettings.GetTargetLanguageFolder());
                Add(command, "@mod", modIdentity);
                Add(command, "@mod_version", modVersionFingerprint ?? string.Empty);
                Add(command, "@domain", (int)domain);
                Add(command, "@fingerprint", analysisFingerprint);
                Add(command, "@completed", (int)WorkflowRunState.Completed);
                return command.ExecuteScalar() != null;
            }
        }

        public bool HasCompletedAnalysis(string modIdentity, CandidateSourceDomain domain)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT 1 FROM AnalysisRuns runs
                    INNER JOIN Mods ON Mods.mod_identity=runs.mod_identity
                    WHERE runs.mod_identity=@mod AND runs.source_domain=@domain
                      AND runs.mod_version_fingerprint=Mods.version_fingerprint
                      AND runs.analyzer_version=CASE WHEN runs.source_domain=0
                          THEN @xml_version ELSE @dll_version END
                      AND runs.state=@completed AND " + CurrentAnalysisRunSql + " LIMIT 1;";
                Add(command, "@analysis_language", WorkflowRuntimeSettings.GetTargetLanguageFolder());
                Add(command, "@mod", modIdentity);
                Add(command, "@domain", (int)domain);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                Add(command, "@completed", (int)WorkflowRunState.Completed);
                return command.ExecuteScalar() != null;
            }
        }

        public bool HasAnyCompletedLocalAnalysis(string modIdentity)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT 1 FROM AnalysisRuns runs
                    INNER JOIN Mods ON Mods.mod_identity=runs.mod_identity
                    WHERE runs.mod_identity=@mod
                      AND runs.mod_version_fingerprint=Mods.version_fingerprint
                      AND runs.analyzer_version=CASE WHEN runs.source_domain=0
                          THEN @xml_version ELSE @dll_version END
                      AND runs.state=@completed AND " + CurrentAnalysisRunSql + " LIMIT 1;";
                Add(command, "@analysis_language", WorkflowRuntimeSettings.GetTargetLanguageFolder());
                Add(command, "@mod", modIdentity);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                Add(command, "@completed", (int)WorkflowRunState.Completed);
                return command.ExecuteScalar() != null;
            }
        }

        public Dictionary<string, HashSet<CandidateSourceDomain>> GetCompletedAnalysisDomains(
            ICollection<string> modIdentities)
        {
            Dictionary<string, HashSet<CandidateSourceDomain>> result =
                new Dictionary<string, HashSet<CandidateSourceDomain>>(StringComparer.Ordinal);
            if (modIdentities == null || modIdentities.Count == 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowAnalysisModsTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"SELECT DISTINCT runs.mod_identity, runs.source_domain
                    FROM AnalysisRuns runs
                    INNER JOIN Mods ON Mods.mod_identity=runs.mod_identity
                    INNER JOIN SelectedWorkflowAnalysisMods selected
                        ON selected.mod_identity=runs.mod_identity
                    WHERE runs.mod_version_fingerprint=Mods.version_fingerprint
                      AND runs.analyzer_version=CASE WHEN runs.source_domain=0
                          THEN @xml_version ELSE @dll_version END
                      AND runs.state=@state AND " + CurrentAnalysisRunSql + ";";
                Add(command, "@analysis_language", WorkflowRuntimeSettings.GetTargetLanguageFolder());
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                Add(command, "@state", (int)WorkflowRunState.Completed);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string modIdentity = Convert.ToString(reader["mod_identity"]);
                        if (!result.TryGetValue(modIdentity, out HashSet<CandidateSourceDomain> domains))
                        {
                            domains = new HashSet<CandidateSourceDomain>();
                            result[modIdentity] = domains;
                        }
                        domains.Add((CandidateSourceDomain)Convert.ToInt32(reader["source_domain"]));
                    }
                }
            }
            return result;
        }

        public Dictionary<string, Dictionary<CandidateSourceDomain, AnalysisRunStatusSummary>>
            GetLatestAnalysisRunStatuses(ICollection<string> modIdentities)
        {
            Dictionary<string, Dictionary<CandidateSourceDomain, AnalysisRunStatusSummary>> result =
                new Dictionary<string, Dictionary<CandidateSourceDomain, AnalysisRunStatusSummary>>(
                    StringComparer.Ordinal);
            if (modIdentities == null || modIdentities.Count == 0) return result;
            Dictionary<string, List<AnalysisRunStatusSummary>> rowsByKey =
                new Dictionary<string, List<AnalysisRunStatusSummary>>(StringComparer.Ordinal);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowAnalysisModsTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"SELECT runs.mod_identity, runs.source_domain, runs.state,
                        runs.error_text, runs.started_utc, runs.completed_utc,
                        runs.analyzer_version, runs.mod_version_fingerprint, runs.target_language,
                        Mods.version_fingerprint AS current_mod_version
                    FROM AnalysisRuns runs
                    INNER JOIN Mods ON Mods.mod_identity=runs.mod_identity
                    INNER JOIN SelectedWorkflowAnalysisMods selected
                        ON selected.mod_identity=runs.mod_identity
                    ORDER BY runs.mod_identity, runs.source_domain,
                        runs.started_utc DESC, runs.rowid DESC;";
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string modIdentity = Convert.ToString(reader["mod_identity"]);
                        CandidateSourceDomain domain =
                            (CandidateSourceDomain)Convert.ToInt32(reader["source_domain"]);
                        string key = modIdentity + "\n" + ((int)domain).ToString();
                        if (!rowsByKey.TryGetValue(key, out List<AnalysisRunStatusSummary> rows))
                        {
                            rows = new List<AnalysisRunStatusSummary>();
                            rowsByKey[key] = rows;
                        }
                        rows.Add(new AnalysisRunStatusSummary
                        {
                            ModIdentity = modIdentity,
                            SourceDomain = domain,
                            State = (WorkflowRunState)Convert.ToInt32(reader["state"]),
                            ErrorText = Convert.ToString(reader["error_text"]),
                            StartedUtc = DateTime.Parse(
                                Convert.ToString(reader["started_utc"])).ToUniversalTime(),
                            CompletedUtc = ParseNullableDbTime(reader["completed_utc"]),
                            AnalyzerVersion = Convert.ToString(reader["analyzer_version"]),
                            ModVersionFingerprint = Convert.ToString(reader["mod_version_fingerprint"]),
                            TargetLanguage = Convert.ToString(reader["target_language"]),
                            CurrentAnalyzerVersion = domain == CandidateSourceDomain.Xml
                                ? WorkflowIdentity.XmlAnalyzerVersion
                                : WorkflowIdentity.DllAnalyzerVersion,
                            CurrentModVersionFingerprint = Convert.ToString(reader["current_mod_version"])
                        });
                    }
                }
            }
            foreach (List<AnalysisRunStatusSummary> rows in rowsByKey.Values)
            {
                AnalysisRunStatusSummary latestSuccess = rows.FirstOrDefault(
                    row => row.State == WorkflowRunState.Completed);
                AnalysisRunStatusSummary current = rows.FirstOrDefault(row =>
                    string.Equals(row.TargetLanguage, WorkflowRuntimeSettings.GetTargetLanguageFolder(), StringComparison.Ordinal) &&
                    (row.State != WorkflowRunState.Completed || ReferenceEquals(row, latestSuccess)) &&
                    string.Equals(row.AnalyzerVersion, row.CurrentAnalyzerVersion, StringComparison.Ordinal) &&
                    string.Equals(row.ModVersionFingerprint, row.CurrentModVersionFingerprint, StringComparison.Ordinal));
                AnalysisRunStatusSummary historicalSuccess = rows.FirstOrDefault(
                    row => row.State == WorkflowRunState.Completed);
                AnalysisRunStatusSummary chosen;
                if (current != null && current.State == WorkflowRunState.Completed)
                {
                    chosen = current;
                    chosen.Freshness = AnalysisResultFreshness.Current;
                }
                else if (current != null && current.State == WorkflowRunState.Failed)
                {
                    chosen = current;
                    chosen.Freshness = AnalysisResultFreshness.Failed;
                }
                else if (historicalSuccess != null)
                {
                    chosen = historicalSuccess;
                    chosen.Freshness = AnalysisResultFreshness.Expired;
                }
                else
                {
                    chosen = rows.First();
                    chosen.Freshness = chosen.State == WorkflowRunState.Failed
                        ? AnalysisResultFreshness.Failed
                        : AnalysisResultFreshness.NeverAnalyzed;
                }
                if (!result.TryGetValue(chosen.ModIdentity,
                        out Dictionary<CandidateSourceDomain, AnalysisRunStatusSummary> byDomain))
                {
                    byDomain = new Dictionary<CandidateSourceDomain, AnalysisRunStatusSummary>();
                    result[chosen.ModIdentity] = byDomain;
                }
                byDomain[chosen.SourceDomain] = chosen;
            }
            return result;
        }

        public List<string> GetModsMissingAnyCompletedLocalAnalysis(
            ICollection<string> modIdentities)
        {
            List<string> result = new List<string>();
            if (modIdentities == null || modIdentities.Count == 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowModsTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"SELECT selected.mod_identity
                    FROM SelectedWorkflowMods selected
                    WHERE NOT EXISTS (
                        SELECT 1 FROM AnalysisRuns runs
                        INNER JOIN Mods ON Mods.mod_identity=runs.mod_identity
                        WHERE runs.mod_identity=selected.mod_identity
                          AND runs.mod_version_fingerprint=Mods.version_fingerprint
                          AND runs.analyzer_version=CASE WHEN runs.source_domain=0
                              THEN @xml_version ELSE @dll_version END
                          AND runs.state=@completed AND " + CurrentAnalysisRunSql + @")
                    ORDER BY selected.mod_identity;";
                Add(command, "@analysis_language", WorkflowRuntimeSettings.GetTargetLanguageFolder());
                Add(command, "@completed", (int)WorkflowRunState.Completed);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(Convert.ToString(reader["mod_identity"]));
            }
            return result;
        }

        public List<string> GetModsWithUnreadyTranslationState(
            ICollection<string> modIdentities,
            string targetLanguage)
        {
            List<string> result = new List<string>();
            if (modIdentities == null || modIdentities.Count == 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowModsTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"SELECT DISTINCT Candidates.mod_identity
                    FROM Candidates
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    INNER JOIN SelectedWorkflowMods selected
                        ON selected.mod_identity=Candidates.mod_identity
                    LEFT JOIN TranslationResults results
                        ON results.candidate_id=Candidates.candidate_id
                        AND results.target_language=@target
                    WHERE Candidates.is_present=1
                      AND " + GetCurrentAnalyzerCandidateSql() + @"
                      AND (results.last_sync_status IN ('ReadError','SourceChanged')
                       OR EXISTS (
                           SELECT 1 FROM PendingFileOperations pending
                           WHERE pending.candidate_id=Candidates.candidate_id
                              AND pending.target_language=@target
                              AND pending.state IN (0,2)))
                    ORDER BY Candidates.mod_identity;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                Add(command, "@target_language", targetLanguage ?? string.Empty);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(Convert.ToString(reader["mod_identity"]));
            }
            return result;
        }

        public void ClearAnalysisResult(string modIdentity, CandidateSourceDomain domain)
        {
            ClassificationLayer layer = domain == CandidateSourceDomain.Xml
                ? ClassificationLayer.Xml
                : ClassificationLayer.Dll;
            int shift = (int)layer * 2;
            int layerMask = 3 << shift;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                using (DbCommand candidates = connection.CreateCommand())
                {
                    candidates.Transaction = transaction;
                    candidates.CommandText = domain == CandidateSourceDomain.Xml
                        ? @"UPDATE Candidates SET
                            classification_flags=(classification_flags & @keep_mask),
                            xml_analyzer_version='', xml_analysis_fingerprint='', updated_utc=@updated
                            WHERE mod_identity=@mod AND source_domain=@domain;"
                        : @"UPDATE Candidates SET
                            classification_flags=(classification_flags & @keep_mask),
                            dll_analyzer_version='', dll_analysis_fingerprint='', updated_utc=@updated
                            WHERE mod_identity=@mod AND source_domain=@domain;";
                    Add(candidates, "@keep_mask", 255 ^ layerMask);
                    Add(candidates, "@updated", ToDbTime(DateTime.UtcNow));
                    Add(candidates, "@mod", modIdentity);
                    Add(candidates, "@domain", (int)domain);
                    candidates.ExecuteNonQuery();
                }
                using (DbCommand runs = connection.CreateCommand())
                {
                    runs.Transaction = transaction;
                    runs.CommandText = @"DELETE FROM AnalysisRuns
                        WHERE mod_identity=@mod AND source_domain=@domain;";
                    Add(runs, "@mod", modIdentity);
                    Add(runs, "@domain", (int)domain);
                    runs.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SaveAnalysisFailure(
            Guid runId,
            string modIdentity,
            string modVersionFingerprint,
            CandidateSourceDomain domain,
            string analyzerVersion,
            string analysisFingerprint,
            bool forced,
            Exception error)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                string now = ToDbTime(DateTime.UtcNow);
                command.CommandText = @"INSERT OR REPLACE INTO AnalysisRuns
                    (run_id, mod_identity, mod_version_fingerprint, source_domain,
                     analyzer_version, analysis_fingerprint, result_fingerprint, diagnostics_json, forced,
                     state, candidate_count, started_utc, completed_utc, error_text, target_language)
                    VALUES (@run, @mod, @mod_version, @domain, @version, @fingerprint, '', '', @forced,
                            @state, 0, @started, @completed, @error, @analysis_language);";
                Add(command, "@analysis_language", WorkflowRuntimeSettings.GetTargetLanguageFolder());
                Add(command, "@run", runId.ToString("N"));
                Add(command, "@mod", modIdentity);
                Add(command, "@mod_version", modVersionFingerprint ?? string.Empty);
                Add(command, "@domain", (int)domain);
                Add(command, "@version", analyzerVersion);
                Add(command, "@fingerprint", analysisFingerprint);
                Add(command, "@forced", forced ? 1 : 0);
                Add(command, "@state", (int)WorkflowRunState.Failed);
                Add(command, "@started", now);
                Add(command, "@completed", now);
                Add(command, "@error", error?.Message ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        public void SaveAnalysisResult(
            Guid runId,
            string modIdentity,
            string modVersionFingerprint,
            CandidateSourceDomain domain,
            string analyzerVersion,
            string analysisFingerprint,
            bool forced,
            IList<CandidateRecord> candidates,
            IList<string> diagnostics)
        {
            AutoTranslatorSettings.AddDebugLog(
                "workflow.analysis persist run=" + runId.ToString("N") + " mod=" + modIdentity +
                " domain=" + domain + " forced=" + forced + " candidates=" + (candidates?.Count ?? 0));
            candidates = candidates ?? Array.Empty<CandidateRecord>();
            ClassificationLayer layer = domain == CandidateSourceDomain.Xml
                ? ClassificationLayer.Xml
                : ClassificationLayer.Dll;
            int layerMask = 3 << ((int)layer * 2);

            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                using (DbCommand markAbsent = connection.CreateCommand())
                {
                    markAbsent.Transaction = transaction;
                    markAbsent.CommandText = @"UPDATE Candidates SET is_present=0, updated_utc=@updated
                        WHERE mod_identity=@mod AND source_domain=@domain AND is_present=1;";
                    Add(markAbsent, "@updated", ToDbTime(DateTime.UtcNow));
                    Add(markAbsent, "@mod", modIdentity);
                    Add(markAbsent, "@domain", (int)domain);
                    markAbsent.ExecuteNonQuery();
                }
                for (int i = 0; i < candidates.Count; i++)
                {
                    CandidateRecord candidate = candidates[i];
                    UpsertCandidate(connection, transaction, candidate, layer, layerMask);
                }

                using (DbCommand run = connection.CreateCommand())
                {
                    run.Transaction = transaction;
                    run.CommandText = @"INSERT OR REPLACE INTO AnalysisRuns
                        (run_id, mod_identity, mod_version_fingerprint, source_domain,
                         analyzer_version, analysis_fingerprint, result_fingerprint, diagnostics_json,
                         forced, state,
                         candidate_count, started_utc, completed_utc, error_text, target_language)
                        VALUES (@run, @mod, @mod_version, @domain, @version, @fingerprint,
                                @result_fingerprint, @diagnostics, @forced, @state,
                                @count, @started, @completed, '', @analysis_language);";
                    Add(run, "@analysis_language", WorkflowRuntimeSettings.GetTargetLanguageFolder());
                    string now = ToDbTime(DateTime.UtcNow);
                    Add(run, "@run", runId.ToString("N"));
                    Add(run, "@mod", modIdentity);
                    Add(run, "@mod_version", modVersionFingerprint ?? string.Empty);
                    Add(run, "@domain", (int)domain);
                    Add(run, "@version", analyzerVersion);
                    Add(run, "@fingerprint", analysisFingerprint);
                    Add(run, "@result_fingerprint",
                        WorkflowIdentity.CreateAnalysisResultFingerprint(candidates));
                    Add(run, "@diagnostics", JsonConvert.SerializeObject(
                        (diagnostics ?? Array.Empty<string>()).Select(value =>
                            LimitText(value, 1000)).ToList()));
                    Add(run, "@forced", forced ? 1 : 0);
                    Add(run, "@state", (int)WorkflowRunState.Completed);
                    Add(run, "@count", candidates.Count);
                    Add(run, "@started", now);
                    Add(run, "@completed", now);
                    run.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public List<CandidateRecord> GetCandidates(ICollection<string> modIdentities, string targetLanguage = null)
        {
            List<CandidateRecord> result = new List<CandidateRecord>();
            if (modIdentities == null || modIdentities.Count == 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowModsTable, ModIdentityColumn, modIdentities);
                Add(command, "@target_language", targetLanguage ?? string.Empty);
                command.CommandText = "SELECT Candidates.*, Mods.package_id AS package_id, " +
                    "COALESCE(TranslationResults.translation_state,0) AS translation_state, " +
                    "COALESCE(TranslationResults.translation_origin,0) AS translation_origin, " +
                    "COALESCE(TranslationResults.translation_text,'') AS translation_text, " +
                    "COALESCE(TranslationResults.translation_hash,'') AS translation_hash, " +
                    "COALESCE(TranslationResults.source_text_hash_at_translation,'') AS source_text_hash_at_translation, " +
                    "COALESCE(TranslationResults.source_package_id,'') AS translation_source_package_id, " +
                    "COALESCE(TranslationResults.source_file_relative_path,'') AS translation_source_file_relative_path, " +
                    "COALESCE(TranslationResults.source_entry_key,'') AS translation_source_entry_key, " +
                    "COALESCE(TranslationResults.managed_output_file,'') AS translation_file_relative_path, " +
                    "COALESCE(NULLIF(TranslationResults.managed_output_key,''),Candidates.output_entry_key,'') AS translation_entry_key, " +
                    "COALESCE(TranslationResults.validation_status,'') AS validation_status, " +
                    "COALESCE(TranslationResults.last_sync_status,'') AS last_sync_status, " +
                    "COALESCE(TranslationResults.last_sync_error,'') AS last_sync_error, " +
                    "COALESCE(TranslationResults.last_synced_utc,'') AS last_synced_utc " +
                    "FROM Candidates " +
                    "INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity " +
                    "LEFT JOIN TranslationResults ON TranslationResults.candidate_id=Candidates.candidate_id " +
                    "AND TranslationResults.target_language=@target_language " +
                    "INNER JOIN SelectedWorkflowMods selected ON selected.mod_identity=Candidates.mod_identity " +
                    "WHERE Candidates.is_present=1 AND " + GetCurrentAnalyzerCandidateSql() + ";";
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(ReadCandidate(reader));
            }
            return result;
        }

        public void ClearClassificationResultsForMods(
            ICollection<string> modIdentities,
            bool clearAiReview,
            bool clearManual,
            WorkflowResultCleanupSummary summary,
            CancellationToken cancellationToken)
        {
            if (modIdentities == null || modIdentities.Count == 0 ||
                (!clearAiReview && !clearManual)) return;
            int aiMask = 3 << ((int)ClassificationLayer.AiReview * 2);
            int manualMask = 3 << ((int)ClassificationLayer.Manual * 2);
            int clearMask = (clearAiReview ? aiMask : 0) | (clearManual ? manualMask : 0);
            string now = ToDbTime(DateTime.UtcNow);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowModsTable, ModIdentityColumn, modIdentities, transaction);
                cancellationToken.ThrowIfCancellationRequested();
                if (clearAiReview)
                    summary.AiReviewCandidateCount = CountSelectedClassificationLayer(
                        connection, transaction, ClassificationLayer.AiReview);
                if (clearManual)
                    summary.ManualClassificationCandidateCount = CountSelectedClassificationLayer(
                        connection, transaction, ClassificationLayer.Manual);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"UPDATE Candidates SET
                        classification_flags=(classification_flags & @keep_mask),
                        ai_review_version=CASE WHEN @clear_ai=1 THEN '' ELSE ai_review_version END,
                        ai_review_prompt_version=CASE WHEN @clear_ai=1 THEN '' ELSE ai_review_prompt_version END,
                        ai_review_fingerprint=CASE WHEN @clear_ai=1 THEN '' ELSE ai_review_fingerprint END,
                        ai_review_reason=CASE WHEN @clear_ai=1 THEN '' ELSE ai_review_reason END,
                        manual_updated_utc=CASE WHEN @clear_manual=1 THEN '' ELSE manual_updated_utc END,
                        updated_utc=@updated
                        WHERE mod_identity IN (SELECT mod_identity FROM SelectedWorkflowMods);";
                    Add(command, "@keep_mask", 255 ^ clearMask);
                    Add(command, "@clear_ai", clearAiReview ? 1 : 0);
                    Add(command, "@clear_manual", clearManual ? 1 : 0);
                    Add(command, "@updated", now);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private static long CountSelectedClassificationLayer(
            DbConnection connection,
            DbTransaction transaction,
            ClassificationLayer layer)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT COUNT(*) FROM Candidates
                    INNER JOIN SelectedWorkflowMods selected
                        ON selected.mod_identity=Candidates.mod_identity
                    WHERE ((Candidates.classification_flags >> @shift) & 3)<>0;";
                Add(command, "@shift", (int)layer * 2);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        public long CountLocalAiTranslations(
            ICollection<string> modIdentities,
            string targetLanguage)
        {
            if (modIdentities == null || modIdentities.Count == 0) return 0L;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowModsTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"SELECT COUNT(*) FROM TranslationResults
                    INNER JOIN Candidates ON Candidates.candidate_id=TranslationResults.candidate_id
                    INNER JOIN SelectedWorkflowMods selected
                        ON selected.mod_identity=Candidates.mod_identity
                    WHERE TranslationResults.target_language=@target
                      AND TranslationResults.translation_origin=@local_ai;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                Add(command, "@local_ai", (int)TranslationOrigin.AiTranslation);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        public LocalAiTranslationPage GetLocalAiTranslationPage(
            ICollection<string> modIdentities,
            string targetLanguage,
            string afterCandidateId,
            int pageSize)
        {
            LocalAiTranslationPage page = new LocalAiTranslationPage();
            if (modIdentities == null || modIdentities.Count == 0) return page;
            pageSize = Math.Max(1, Math.Min(1000, pageSize));
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowModsTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"SELECT Candidates.*, Mods.package_id AS package_id,
                    TranslationResults.translation_state AS translation_state,
                    TranslationResults.translation_origin AS translation_origin,
                    TranslationResults.translation_text AS translation_text,
                    TranslationResults.translation_hash AS translation_hash,
                    TranslationResults.source_text_hash_at_translation AS source_text_hash_at_translation,
                    TranslationResults.source_package_id AS translation_source_package_id,
                    TranslationResults.source_file_relative_path AS translation_source_file_relative_path,
                    TranslationResults.source_entry_key AS translation_source_entry_key,
                    TranslationResults.managed_output_file AS translation_file_relative_path,
                    COALESCE(NULLIF(TranslationResults.managed_output_key,''),Candidates.output_entry_key,'')
                        AS translation_entry_key,
                    TranslationResults.validation_status AS validation_status,
                    TranslationResults.last_sync_status AS last_sync_status,
                    TranslationResults.last_sync_error AS last_sync_error,
                    TranslationResults.last_synced_utc AS last_synced_utc
                    FROM TranslationResults
                    INNER JOIN Candidates ON Candidates.candidate_id=TranslationResults.candidate_id
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    INNER JOIN SelectedWorkflowMods selected
                        ON selected.mod_identity=Candidates.mod_identity
                    WHERE TranslationResults.target_language=@target
                      AND TranslationResults.translation_origin=@local_ai
                      AND Candidates.candidate_id>@cursor
                    ORDER BY Candidates.candidate_id
                    LIMIT @limit;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                Add(command, "@local_ai", (int)TranslationOrigin.AiTranslation);
                Add(command, "@cursor", afterCandidateId ?? string.Empty);
                Add(command, "@limit", pageSize + 1);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) page.Candidates.Add(ReadCandidate(reader));
            }
            page.HasMore = page.Candidates.Count > pageSize;
            if (page.HasMore) page.Candidates.RemoveAt(page.Candidates.Count - 1);
            CandidateRecord last = page.Candidates.LastOrDefault();
            if (last != null) page.NextCandidateId = last.CandidateId;
            return page;
        }

        public CandidatePage GetEditorCandidatePage(
            string modIdentity,
            string targetLanguage,
            string search,
            CandidateClassification? classification,
            int translationFilter,
            int offset,
            int limit,
            bool requireManualClassification = false)
        {
            CandidatePage page = new CandidatePage();
            offset = Math.Max(0, offset);
            limit = Math.Max(1, Math.Min(1000, limit));
            string effective = @"CASE
                WHEN ((Candidates.classification_flags >> 6) & 3) <> 0
                    THEN ((Candidates.classification_flags >> 6) & 3)
                WHEN ((Candidates.classification_flags >> 4) & 3) <> 0
                    THEN ((Candidates.classification_flags >> 4) & 3)
                WHEN Candidates.source_domain=1 AND Candidates.dll_analyzer_version=@dll_version
                    THEN ((Candidates.classification_flags >> 2) & 3)
                WHEN Candidates.source_domain=0 AND Candidates.xml_analyzer_version=@xml_version
                    THEN (Candidates.classification_flags & 3)
                ELSE 0 END";
            string where = @" FROM Candidates
                INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                LEFT JOIN TranslationResults ON TranslationResults.candidate_id=Candidates.candidate_id
                    AND TranslationResults.target_language=@target_language
                WHERE Candidates.is_present=1 AND Candidates.mod_identity=@mod AND " +
                GetCurrentAnalyzerCandidateSql();
            if (!string.IsNullOrWhiteSpace(search))
                where += @" AND (Candidates.logical_locator LIKE @search OR Candidates.source_text LIKE @search
                    OR Candidates.source_file_relative_path LIKE @search
                    OR COALESCE(TranslationResults.translation_text,'') LIKE @search)";
            if (classification.HasValue) where += " AND (" + effective + ")=@classification";
            if (requireManualClassification)
                where += " AND ((Candidates.classification_flags >> 6) & 3)<>0";
            if (translationFilter == 1)
                where += " AND (" + effective + @")=2
                    AND COALESCE(TranslationResults.translation_state,0) NOT IN (1,3)
                    AND NOT " + GetTranslationCurrentSql();
            else if (translationFilter == 2)
                where += " AND (" + effective + ")=2 AND " + GetTranslationCurrentSql();
            else if (translationFilter == 3)
                where += " AND (" + effective + ")=2" +
                         " AND COALESCE(TranslationResults.translation_state,0)=3" +
                         " AND NOT " + GetTranslationCurrentSql();

            using (DbConnection connection = _connections.OpenConnection())
            {
                using (DbCommand count = connection.CreateCommand())
                {
                    count.CommandText = "SELECT COUNT(*)" + where;
                    AddEditorPageParameters(count, modIdentity, targetLanguage, search, classification);
                    page.TotalCount = Convert.ToInt32(count.ExecuteScalar());
                }
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Candidates.*, Mods.package_id AS package_id, " +
                        "COALESCE(TranslationResults.translation_state,0) AS translation_state, " +
                        "COALESCE(TranslationResults.translation_origin,0) AS translation_origin, " +
                        "COALESCE(TranslationResults.translation_text,'') AS translation_text, " +
                        "COALESCE(TranslationResults.translation_hash,'') AS translation_hash, " +
                        "COALESCE(TranslationResults.source_text_hash_at_translation,'') AS source_text_hash_at_translation, " +
                        "COALESCE(TranslationResults.source_package_id,'') AS translation_source_package_id, " +
                        "COALESCE(TranslationResults.source_file_relative_path,'') AS translation_source_file_relative_path, " +
                        "COALESCE(TranslationResults.source_entry_key,'') AS translation_source_entry_key, " +
                        "COALESCE(TranslationResults.managed_output_file,'') AS translation_file_relative_path, " +
                        "COALESCE(NULLIF(TranslationResults.managed_output_key,''),Candidates.output_entry_key,'') AS translation_entry_key, " +
                        "COALESCE(TranslationResults.validation_status,'') AS validation_status, " +
                        "COALESCE(TranslationResults.error_text,'') AS translation_error, " +
                        "COALESCE(TranslationResults.last_sync_status,'') AS last_sync_status, " +
                        "COALESCE(TranslationResults.last_sync_error,'') AS last_sync_error, " +
                        "COALESCE(TranslationResults.last_synced_utc,'') AS last_synced_utc" + where +
                        @" ORDER BY Candidates.source_domain, Candidates.source_file_relative_path,
                            Candidates.source_line_number, Candidates.logical_locator, Candidates.candidate_id
                            LIMIT @limit OFFSET @offset;";
                    AddEditorPageParameters(command, modIdentity, targetLanguage, search, classification);
                    Add(command, "@limit", limit);
                    Add(command, "@offset", offset);
                    using (DbDataReader reader = command.ExecuteReader())
                        while (reader.Read()) page.Candidates.Add(ReadCandidate(reader));
                }
            }
            return page;
        }

        private static void AddEditorPageParameters(
            DbCommand command, string modIdentity, string targetLanguage, string search,
            CandidateClassification? classification)
        {
            Add(command, "@mod", modIdentity ?? string.Empty);
            Add(command, "@target_language", targetLanguage ?? string.Empty);
            Add(command, "@search", "%" + (search ?? string.Empty).Trim() + "%");
            Add(command, "@classification", classification.HasValue ? (int)classification.Value : 0);
            Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
            Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
        }

        public HashSet<string> GetProtectedTranslationTargetIdentities(ICollection<string> modIdentities)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            if (modIdentities == null || modIdentities.Count == 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedProtectedCheckTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"SELECT Mods.mod_identity, Mods.package_id FROM Mods
                    INNER JOIN SelectedProtectedCheck selected ON selected.mod_identity=Mods.mod_identity;";
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                        if (AutoTranslatorScanner.IsNonTranslatableSystemPackage(Convert.ToString(reader["package_id"])))
                            result.Add(Convert.ToString(reader["mod_identity"]));
            }
            return result;
        }

        public long CountAiCandidates(
            string modIdentity,
            string targetLanguage,
            ICollection<CandidateClassification> classifications,
            bool requireTranslationNotCurrent,
            ICollection<string> candidateIds = null,
            bool requireTranslationReady = false,
            bool requireAiReviewMissing = false)
        {
            List<CandidateClassification> selected = (classifications ?? Array.Empty<CandidateClassification>())
                .Distinct().ToList();
            if (string.IsNullOrWhiteSpace(modIdentity) || selected.Count == 0) return 0L;
            bool restrictCandidates = candidateIds != null;
            List<string> selectedCandidateIds = (candidateIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToList();
            if (restrictCandidates && selectedCandidateIds.Count == 0) return 0L;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                if (restrictCandidates)
                    CreateAndFillTemporarySelection(
                        connection, SelectedWorkflowCandidatesTable, CandidateIdColumn, selectedCandidateIds);
                string effective = GetEffectiveClassificationSql();
                command.CommandText = "SELECT COUNT(*) FROM Candidates " +
                    "INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity " +
                    (restrictCandidates
                        ? "INNER JOIN SelectedWorkflowCandidates selectedCandidates " +
                          "ON selectedCandidates.candidate_id=Candidates.candidate_id "
                        : string.Empty) +
                    "LEFT JOIN TranslationResults ON TranslationResults.candidate_id=Candidates.candidate_id " +
                    "AND TranslationResults.target_language=@target_language " +
                    "WHERE Candidates.is_present=1 AND Candidates.mod_identity=@mod " +
                    "AND " + GetCurrentAnalyzerCandidateSql() + " " +
                    "AND (" + effective + ") IN (" + AddClassificationParameters(command, selected) + ")" +
                    (requireAiReviewMissing
                        ? " AND ((Candidates.classification_flags >> 4) & 3)=0"
                        : string.Empty) +
                    (requireTranslationReady || requireTranslationNotCurrent
                        ? " AND " + GetTranslationReadySql()
                        : string.Empty) +
                    (requireTranslationNotCurrent
                        ? " AND NOT " + GetTranslationCurrentSql()
                        : string.Empty) + ";";
                AddAiPageCommonParameters(command, modIdentity, targetLanguage);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        public AiCandidatePage GetAiCandidatePage(
            string modIdentity,
            string targetLanguage,
            ICollection<CandidateClassification> classifications,
            bool requireTranslationNotCurrent,
            AiCandidateCursor cursor,
            int pageSize,
            ICollection<string> candidateIds = null,
            bool requireAiReviewMissing = false)
        {
            AiCandidatePage page = new AiCandidatePage();
            List<CandidateClassification> selected = (classifications ?? Array.Empty<CandidateClassification>())
                .Distinct().ToList();
            if (string.IsNullOrWhiteSpace(modIdentity) || selected.Count == 0) return page;
            bool restrictCandidates = candidateIds != null;
            List<string> selectedCandidateIds = (candidateIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToList();
            if (restrictCandidates && selectedCandidateIds.Count == 0) return page;
            pageSize = Math.Max(1, Math.Min(1000, pageSize));
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                if (restrictCandidates)
                    CreateAndFillTemporarySelection(
                        connection, SelectedWorkflowCandidatesTable, CandidateIdColumn, selectedCandidateIds);
                string effective = GetEffectiveClassificationSql();
                string cursorWhere = cursor == null ? string.Empty : @" AND (
                    Candidates.source_file_relative_path>@cursor_file OR
                    (Candidates.source_file_relative_path=@cursor_file AND Candidates.source_line_number>@cursor_line) OR
                    (Candidates.source_file_relative_path=@cursor_file AND Candidates.source_line_number=@cursor_line
                        AND Candidates.candidate_id>@cursor_id))";
                command.CommandText = "SELECT Candidates.*, Mods.package_id AS package_id, " +
                    "COALESCE(TranslationResults.translation_state,0) AS translation_state, " +
                    "COALESCE(TranslationResults.translation_origin,0) AS translation_origin, " +
                    "COALESCE(TranslationResults.translation_text,'') AS translation_text, " +
                    "COALESCE(TranslationResults.translation_hash,'') AS translation_hash, " +
                    "COALESCE(TranslationResults.source_text_hash_at_translation,'') AS source_text_hash_at_translation, " +
                    "COALESCE(TranslationResults.source_package_id,'') AS translation_source_package_id, " +
                    "COALESCE(TranslationResults.source_file_relative_path,'') AS translation_source_file_relative_path, " +
                    "COALESCE(TranslationResults.source_entry_key,'') AS translation_source_entry_key, " +
                    "COALESCE(TranslationResults.managed_output_file,'') AS translation_file_relative_path, " +
                    "COALESCE(NULLIF(TranslationResults.managed_output_key,''),Candidates.output_entry_key,'') " +
                    "AS translation_entry_key, " +
                    "COALESCE(TranslationResults.validation_status,'') AS validation_status, " +
                    "COALESCE(TranslationResults.error_text,'') AS translation_error, " +
                    "'' AS last_sync_status, '' AS last_sync_error, '' AS last_synced_utc " +
                    "FROM Candidates INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity " +
                    (restrictCandidates
                        ? "INNER JOIN SelectedWorkflowCandidates selectedCandidates " +
                          "ON selectedCandidates.candidate_id=Candidates.candidate_id "
                        : string.Empty) +
                    "LEFT JOIN TranslationResults ON TranslationResults.candidate_id=Candidates.candidate_id " +
                    "AND TranslationResults.target_language=@target_language " +
                    "WHERE Candidates.is_present=1 AND Candidates.mod_identity=@mod " +
                    "AND " + GetCurrentAnalyzerCandidateSql() + " " +
                    "AND (" + effective + ") IN (" + AddClassificationParameters(command, selected) + ")" +
                    (requireAiReviewMissing
                        ? " AND ((Candidates.classification_flags >> 4) & 3)=0"
                        : string.Empty) +
                    (requireTranslationNotCurrent
                        ? " AND " + GetTranslationReadySql() + " AND NOT " + GetTranslationCurrentSql()
                        : string.Empty) +
                    cursorWhere + @" ORDER BY Candidates.source_file_relative_path,
                        Candidates.source_line_number, Candidates.candidate_id LIMIT @limit;";
                AddAiPageCommonParameters(command, modIdentity, targetLanguage);
                Add(command, "@cursor_file", cursor?.SourceFileRelativePath ?? string.Empty);
                Add(command, "@cursor_line", cursor?.SourceLineNumber ?? 0);
                Add(command, "@cursor_id", cursor?.CandidateId ?? string.Empty);
                Add(command, "@limit", pageSize + 1);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) page.Candidates.Add(ReadCandidate(reader));
            }
            page.HasMore = page.Candidates.Count > pageSize;
            if (page.HasMore) page.Candidates.RemoveAt(page.Candidates.Count - 1);
            CandidateRecord last = page.Candidates.LastOrDefault();
            if (last != null)
                page.NextCursor = new AiCandidateCursor
                {
                    SourceFileRelativePath = last.SourceFileRelativePath,
                    SourceLineNumber = last.SourceLineNumber,
                    CandidateId = last.CandidateId
                };
            return page;
        }

        private static string GetEffectiveClassificationSql()
        {
            return @"CASE
                    WHEN ((Candidates.classification_flags >> 6) & 3)<>0 THEN ((Candidates.classification_flags >> 6) & 3)
                    WHEN ((Candidates.classification_flags >> 4) & 3)<>0
                        THEN ((Candidates.classification_flags >> 4) & 3)
                    WHEN ((Candidates.classification_flags >> 2) & 3)<>0
                         AND Candidates.source_domain=1 AND Candidates.dll_analyzer_version=@dll_version
                        THEN ((Candidates.classification_flags >> 2) & 3)
                    WHEN (Candidates.classification_flags & 3)<>0
                         AND Candidates.source_domain=0 AND Candidates.xml_analyzer_version=@xml_version
                        THEN (Candidates.classification_flags & 3)
                    ELSE 1 END";
        }

        private static string GetCurrentAnalyzerCandidateSql()
        {
            return @"(Candidates.mod_version_fingerprint=Mods.version_fingerprint AND
                  EXISTS (SELECT 1 FROM AnalysisRuns current_run
                      WHERE current_run.mod_identity=Candidates.mod_identity
                        AND current_run.source_domain=Candidates.source_domain
                        AND current_run.state=1 AND current_run.target_language=@target_language
                        AND current_run.analysis_fingerprint=CASE WHEN Candidates.source_domain=0
                            THEN Candidates.xml_analysis_fingerprint ELSE Candidates.dll_analysis_fingerprint END
                        AND current_run.rowid=(SELECT MAX(latest.rowid) FROM AnalysisRuns latest
                            WHERE latest.mod_identity=current_run.mod_identity
                              AND latest.source_domain=current_run.source_domain AND latest.state=1)) AND
                  ((Candidates.source_domain=0 AND Candidates.xml_analyzer_version=@xml_version)
                   OR (Candidates.source_domain=1 AND Candidates.dll_analyzer_version=@dll_version)))";
        }

        private static string GetTranslationCurrentSql()
        {
            return @"(Candidates.source_text_hash<>'' AND
                COALESCE(TranslationResults.source_text_hash_at_translation,'')=Candidates.source_text_hash AND
                COALESCE(TranslationResults.translation_text,'')<>'' AND
                (COALESCE(TranslationResults.translation_state,0)=2 OR
                 COALESCE(TranslationResults.translation_origin,0)>1))";
        }

        private static string GetTranslationReadySql()
        {
            return @"(COALESCE(TranslationResults.last_sync_status,'') NOT IN ('ReadError','SourceChanged')
                AND NOT EXISTS (
                    SELECT 1 FROM PendingFileOperations pending
                    WHERE pending.candidate_id=Candidates.candidate_id
                      AND pending.target_language=@target_language
                      AND pending.state IN (0,2)))";
        }

        private static string AddClassificationParameters(
            DbCommand command, IList<CandidateClassification> classifications)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < classifications.Count; i++)
            {
                string name = "@class" + i;
                names.Add(name);
                Add(command, name, (int)classifications[i]);
            }
            return string.Join(",", names);
        }

        private static void AddAiPageCommonParameters(
            DbCommand command, string modIdentity, string targetLanguage)
        {
            Add(command, "@mod", modIdentity ?? string.Empty);
            Add(command, "@target_language", targetLanguage ?? string.Empty);
            Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
            Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
        }

        public List<WorkbenchModAggregate> GetWorkbenchModAggregates(
            ICollection<string> modIdentities,
            string targetLanguage)
        {
            List<WorkbenchModAggregate> result = new List<WorkbenchModAggregate>();
            if (modIdentities == null || modIdentities.Count == 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowModsTable, ModIdentityColumn, modIdentities);
                command.CommandText = @"WITH CurrentCandidates AS (
                        SELECT Candidates.mod_identity,
                               Candidates.source_domain,
                               Candidates.source_text_hash,
                               CASE
                                   WHEN Candidates.source_domain=0
                                        AND Candidates.xml_analyzer_version=@xml_version
                                       THEN (Candidates.classification_flags & 3)
                                   WHEN Candidates.source_domain=1
                                        AND Candidates.dll_analyzer_version=@dll_version
                                       THEN ((Candidates.classification_flags >> 2) & 3)
                                   ELSE 0
                               END AS local_classification,
                               CASE
                                   WHEN ((Candidates.classification_flags >> 6) & 3) <> 0
                                       THEN ((Candidates.classification_flags >> 6) & 3)
                                    WHEN ((Candidates.classification_flags >> 4) & 3) <> 0
                                       THEN ((Candidates.classification_flags >> 4) & 3)
                                   WHEN ((Candidates.classification_flags >> 2) & 3) <> 0
                                        AND Candidates.dll_analyzer_version=@dll_version
                                       THEN ((Candidates.classification_flags >> 2) & 3)
                                   WHEN (Candidates.classification_flags & 3) <> 0
                                        AND Candidates.xml_analyzer_version=@xml_version
                                       THEN (Candidates.classification_flags & 3)
                                   ELSE 1
                               END AS effective_classification,
                               COALESCE(TranslationResults.translation_state, 0) AS translation_state,
                               COALESCE(TranslationResults.translation_origin, 0) AS translation_origin,
                               COALESCE(TranslationResults.translation_text, '') AS translation_text,
                               COALESCE(TranslationResults.source_text_hash_at_translation, '')
                                   AS translated_source_hash
                        FROM Candidates
                        INNER JOIN SelectedWorkflowMods selected
                            ON selected.mod_identity=Candidates.mod_identity
                        INNER JOIN Mods
                            ON Mods.mod_identity=Candidates.mod_identity
                        LEFT JOIN TranslationResults
                            ON TranslationResults.candidate_id=Candidates.candidate_id
                           AND TranslationResults.target_language=@target
                        WHERE Candidates.is_present=1
                          AND " + GetCurrentAnalyzerCandidateSql() + @"
                    )
                    SELECT mod_identity, source_domain,
                           COUNT(*) AS candidate_count,
                           SUM(CASE WHEN local_classification=2 THEN 1 ELSE 0 END) AS local_needs,
                           SUM(CASE WHEN local_classification=1 THEN 1 ELSE 0 END) AS local_undetermined,
                           SUM(CASE WHEN local_classification=3 THEN 1 ELSE 0 END) AS local_no_need,
                           SUM(CASE WHEN effective_classification=2 THEN 1 ELSE 0 END) AS effective_needs,
                           SUM(CASE WHEN effective_classification=1 THEN 1 ELSE 0 END) AS effective_undetermined,
                           SUM(CASE WHEN effective_classification=3 THEN 1 ELSE 0 END) AS effective_no_need,
                           SUM(CASE WHEN effective_classification=2
                                         AND translation_state=1
                                         AND NOT (source_text_hash<>''
                                                  AND source_text_hash=translated_source_hash
                                                  AND translation_origin>1
                                                  AND translation_text<>'')
                                    THEN 1 ELSE 0 END) AS translating,
                           SUM(CASE WHEN effective_classification=2
                                         AND source_text_hash<>''
                                         AND source_text_hash=translated_source_hash
                                         AND translation_text<>''
                                         AND (translation_state=2 OR
                                              translation_origin>1)
                                    THEN 1 ELSE 0 END) AS translated,
                           SUM(CASE WHEN effective_classification=2
                                         AND translation_state=3
                                         AND NOT (source_text_hash<>''
                                                  AND source_text_hash=translated_source_hash
                                                  AND translation_origin>1
                                                  AND translation_text<>'')
                                    THEN 1 ELSE 0 END) AS failed,
                           SUM(CASE WHEN effective_classification=2
                                         AND translation_state<>1
                                         AND NOT (source_text_hash<>''
                                                  AND source_text_hash=translated_source_hash
                                                  AND translation_text<>''
                                                  AND (translation_state=2 OR translation_origin>1))
                                         AND translation_state<>3
                                    THEN 1 ELSE 0 END) AS untranslated
                    FROM CurrentCandidates
                    GROUP BY mod_identity, source_domain
                    ORDER BY mod_identity, source_domain;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                Add(command, "@target_language", targetLanguage ?? string.Empty);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new WorkbenchModAggregate
                        {
                            ModIdentity = Convert.ToString(reader["mod_identity"]),
                            SourceDomain = (CandidateSourceDomain)Convert.ToInt32(reader["source_domain"]),
                            CandidateCount = Convert.ToInt32(reader["candidate_count"]),
                            LocalNeedsTranslation = Convert.ToInt32(reader["local_needs"]),
                            LocalUndetermined = Convert.ToInt32(reader["local_undetermined"]),
                            LocalNoTranslationNeeded = Convert.ToInt32(reader["local_no_need"]),
                            EffectiveNeedsTranslation = Convert.ToInt32(reader["effective_needs"]),
                            EffectiveUndetermined = Convert.ToInt32(reader["effective_undetermined"]),
                            EffectiveNoTranslationNeeded = Convert.ToInt32(reader["effective_no_need"]),
                            Untranslated = Convert.ToInt32(reader["untranslated"]),
                            Translating = Convert.ToInt32(reader["translating"]),
                            Translated = Convert.ToInt32(reader["translated"]),
                            Failed = Convert.ToInt32(reader["failed"])
                        });
                    }
                }
            }
            return result;
        }

        public int CountCandidatesWithTranslationBindings(
            string targetLanguage,
            ICollection<string> changedRelativePaths)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, ChangedTranslationFilesTable, RelativePathColumn,
                    changedRelativePaths ?? Array.Empty<string>());
                command.CommandText = "SELECT COUNT(*) FROM (" +
                    GetTranslationSynchronizationScopeSql() + ") ScopedCandidates;";
                AddTranslationSynchronizationScopeParameters(command, targetLanguage);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        public List<CandidateRecord> GetCandidatesWithTranslationBindingsPage(
            string targetLanguage,
            string afterCandidateId,
            int pageSize,
            ICollection<string> changedRelativePaths)
        {
            List<CandidateRecord> result = new List<CandidateRecord>();
            if (pageSize <= 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, ChangedTranslationFilesTable, RelativePathColumn,
                    changedRelativePaths ?? Array.Empty<string>());
                command.CommandText = @"WITH ScopedCandidates AS (" +
                    GetTranslationSynchronizationScopeSql() +
                    @") SELECT Candidates.*, Mods.package_id AS package_id,
                    COALESCE(TranslationResults.translation_state,0) AS translation_state,
                    COALESCE(TranslationResults.translation_origin,0) AS translation_origin,
                    COALESCE(TranslationResults.translation_text,'') AS translation_text,
                    COALESCE(TranslationResults.translation_hash,'') AS translation_hash,
                    COALESCE(TranslationResults.source_text_hash_at_translation,'') AS source_text_hash_at_translation,
                    COALESCE(TranslationResults.source_package_id,'') AS translation_source_package_id,
                    COALESCE(TranslationResults.source_file_relative_path,'') AS translation_source_file_relative_path,
                    COALESCE(TranslationResults.source_entry_key,'') AS translation_source_entry_key,
                    COALESCE(TranslationResults.managed_output_file,'') AS translation_file_relative_path,
                    COALESCE(NULLIF(TranslationResults.managed_output_key,''),Candidates.output_entry_key,'') AS translation_entry_key,
                    COALESCE(TranslationResults.validation_status,'') AS validation_status,
                    COALESCE(TranslationResults.last_sync_status,'') AS last_sync_status,
                    COALESCE(TranslationResults.last_sync_error,'') AS last_sync_error,
                    COALESCE(TranslationResults.last_synced_utc,'') AS last_synced_utc
                    FROM ScopedCandidates
                    INNER JOIN Candidates ON Candidates.candidate_id=ScopedCandidates.candidate_id
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    LEFT JOIN TranslationResults ON TranslationResults.candidate_id=Candidates.candidate_id
                        AND TranslationResults.target_language=@target_language
                    WHERE Candidates.candidate_id>@after_candidate_id COLLATE BINARY
                    ORDER BY Candidates.candidate_id COLLATE BINARY
                    LIMIT @page_size;";
                AddTranslationSynchronizationScopeParameters(command, targetLanguage);
                Add(command, "@after_candidate_id", afterCandidateId ?? string.Empty);
                Add(command, "@page_size", pageSize);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(ReadCandidate(reader));
            }
            return result;
        }

        public int CountCurrentManagedTranslations(string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT COUNT(*)
                    FROM Candidates
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    INNER JOIN TranslationResults
                        ON TranslationResults.candidate_id=Candidates.candidate_id
                       AND TranslationResults.target_language=@target_language
                    WHERE Candidates.is_present=1
                      AND Candidates.mod_version_fingerprint=Mods.version_fingerprint
                      AND TranslationResults.translation_state=@translated
                      AND TranslationResults.translation_text<>''
                      AND TranslationResults.source_text_hash_at_translation=Candidates.source_text_hash
                      AND TranslationResults.managed_output_file<>''
                      AND TranslationResults.managed_output_key<>''
                      AND ((Candidates.source_domain=0
                            AND Candidates.xml_analyzer_version=@xml_version)
                           OR (Candidates.source_domain=1
                               AND Candidates.dll_analyzer_version=@dll_version));";
                Add(command, "@target_language", targetLanguage ?? string.Empty);
                Add(command, "@translated", (int)CandidateTranslationState.Translated);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        public List<CandidateRecord> GetCurrentManagedTranslationsPage(
            string targetLanguage,
            string afterCandidateId,
            int pageSize,
            bool changedSourcesOnly = false)
        {
            List<CandidateRecord> result = new List<CandidateRecord>();
            if (pageSize <= 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT Candidates.*, Mods.package_id AS package_id,
                    TranslationResults.translation_state AS translation_state,
                    TranslationResults.translation_origin AS translation_origin,
                    TranslationResults.translation_text AS translation_text,
                    TranslationResults.translation_hash AS translation_hash,
                    TranslationResults.source_text_hash_at_translation AS source_text_hash_at_translation,
                    TranslationResults.source_package_id AS translation_source_package_id,
                    TranslationResults.source_file_relative_path AS translation_source_file_relative_path,
                    TranslationResults.source_entry_key AS translation_source_entry_key,
                    TranslationResults.managed_output_file AS translation_file_relative_path,
                    TranslationResults.managed_output_key AS translation_entry_key,
                    TranslationResults.validation_status AS validation_status,
                    TranslationResults.error_text AS translation_error,
                    TranslationResults.last_sync_status AS last_sync_status,
                    TranslationResults.last_sync_error AS last_sync_error,
                    TranslationResults.last_synced_utc AS last_synced_utc
                    FROM Candidates
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    INNER JOIN TranslationResults
                        ON TranslationResults.candidate_id=Candidates.candidate_id
                       AND TranslationResults.target_language=@target_language
                    WHERE Candidates.candidate_id>@after_candidate_id COLLATE BINARY
                      AND (@changed_sources_only=0 OR TranslationResults.last_sync_status='SourceChanged')
                      AND Candidates.is_present=1
                      AND Candidates.mod_version_fingerprint=Mods.version_fingerprint
                      AND TranslationResults.translation_state=@translated
                      AND TranslationResults.translation_text<>''
                      AND TranslationResults.source_text_hash_at_translation=Candidates.source_text_hash
                      AND TranslationResults.managed_output_file<>''
                      AND TranslationResults.managed_output_key<>''
                      AND ((Candidates.source_domain=0
                            AND Candidates.xml_analyzer_version=@xml_version)
                           OR (Candidates.source_domain=1
                               AND Candidates.dll_analyzer_version=@dll_version))
                    ORDER BY Candidates.candidate_id COLLATE BINARY
                    LIMIT @page_size;";
                Add(command, "@target_language", targetLanguage ?? string.Empty);
                Add(command, "@after_candidate_id", afterCandidateId ?? string.Empty);
                Add(command, "@changed_sources_only", changedSourcesOnly ? 1 : 0);
                Add(command, "@translated", (int)CandidateTranslationState.Translated);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                Add(command, "@page_size", pageSize);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(ReadCandidate(reader));
            }
            return result;
        }

        private static string GetTranslationSynchronizationScopeSql()
        {
            // Manual refresh is driven by changed translation files.  Startup injection
            // never calls this query.  Current candidates are matched by their canonical
            // output path.  The second arm only adds another managed output path for
            // the same current candidates; expired analyzer rows never re-enter sync.
            return @"SELECT Candidates.candidate_id
                    FROM Candidates
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    INNER JOIN ChangedTranslationFiles changed
                        ON changed.relative_path=Candidates.output_relative_path
                    WHERE Candidates.output_entry_key<>''
                      AND Candidates.is_present=1
                      AND Candidates.mod_version_fingerprint=Mods.version_fingerprint
                      AND ((Candidates.source_domain=0
                            AND Candidates.xml_analyzer_version=@xml_version)
                           OR (Candidates.source_domain=1
                               AND Candidates.dll_analyzer_version=@dll_version))
                    UNION
                    SELECT Candidates.candidate_id
                    FROM Candidates
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    INNER JOIN TranslationResults
                        ON TranslationResults.candidate_id=Candidates.candidate_id
                       AND TranslationResults.target_language=@target_language
                    INNER JOIN ChangedTranslationFiles changed
                        ON changed.relative_path=COALESCE(
                            NULLIF(TranslationResults.managed_output_file,''),
                            Candidates.output_relative_path)
                    WHERE Candidates.output_entry_key<>''
                      AND Candidates.is_present=1
                      AND Candidates.mod_version_fingerprint=Mods.version_fingerprint
                      AND ((Candidates.source_domain=0
                            AND Candidates.xml_analyzer_version=@xml_version)
                           OR (Candidates.source_domain=1
                               AND Candidates.dll_analyzer_version=@dll_version))";
        }

        private static void AddTranslationSynchronizationScopeParameters(
            DbCommand command,
            string targetLanguage)
        {
            Add(command, "@target_language", targetLanguage ?? string.Empty);
            Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
            Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
        }

        public CandidateRecord GetCandidate(string candidateId, string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT Candidates.*, Mods.package_id AS package_id,
                    COALESCE(TranslationResults.translation_state,0) AS translation_state,
                    COALESCE(TranslationResults.translation_origin,0) AS translation_origin,
                    COALESCE(TranslationResults.translation_text,'') AS translation_text,
                    COALESCE(TranslationResults.translation_hash,'') AS translation_hash,
                    COALESCE(TranslationResults.source_text_hash_at_translation,'') AS source_text_hash_at_translation,
                    COALESCE(TranslationResults.source_package_id,'') AS translation_source_package_id,
                    COALESCE(TranslationResults.source_file_relative_path,'') AS translation_source_file_relative_path,
                    COALESCE(TranslationResults.source_entry_key,'') AS translation_source_entry_key,
                    COALESCE(TranslationResults.managed_output_file,'') AS translation_file_relative_path,
                    COALESCE(NULLIF(TranslationResults.managed_output_key,''),Candidates.output_entry_key,'') AS translation_entry_key,
                    COALESCE(TranslationResults.validation_status,'') AS validation_status,
                    COALESCE(TranslationResults.last_sync_status,'') AS last_sync_status,
                    COALESCE(TranslationResults.last_sync_error,'') AS last_sync_error,
                    COALESCE(TranslationResults.last_synced_utc,'') AS last_synced_utc
                    FROM Candidates INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    LEFT JOIN TranslationResults ON TranslationResults.candidate_id=Candidates.candidate_id
                        AND TranslationResults.target_language=@target
                    WHERE Candidates.candidate_id=@id LIMIT 1;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                Add(command, "@id", candidateId);
                using (DbDataReader reader = command.ExecuteReader())
                    return reader.Read() ? ReadCandidate(reader) : null;
            }
        }

        public List<string> GetModIdentitiesByPackageIds(IEnumerable<string> packageIds)
        {
            List<string> packages = (packageIds ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> result = new List<string>();
            if (packages.Count == 0) return result;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowPackagesTable, PackageIdColumn, packages);
                command.CommandText = @"SELECT Mods.mod_identity FROM Mods
                    INNER JOIN SelectedWorkflowPackages selected
                        ON selected.package_id=Mods.package_id COLLATE NOCASE
                    ORDER BY Mods.package_id;";
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(Convert.ToString(reader["mod_identity"]));
            }
            return result;
        }

        public bool TryGetObservedTranslationEntry(
            string relativePath,
            string entryKey,
            string targetLanguage,
            out string observedHash)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT observed_hash FROM TranslationFileEntries
                    WHERE relative_path=@path AND entry_key=@key AND target_language=@target LIMIT 1;";
                Add(command, "@path", relativePath);
                Add(command, "@key", entryKey);
                Add(command, "@target", targetLanguage);
                object value = command.ExecuteScalar();
                observedHash = value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value);
                return value != null && value != DBNull.Value;
            }
        }

        public Dictionary<string, string> GetObservedTranslationHashes(string targetLanguage)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.Ordinal);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT relative_path, entry_key, observed_hash
                    FROM TranslationFileEntries WHERE target_language=@target;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string key = TranslationFileIndexResult.CreateKey(
                            Convert.ToString(reader["relative_path"]),
                            Convert.ToString(reader["entry_key"]));
                        result[key] = Convert.ToString(reader["observed_hash"]);
                    }
                }
            }
            return result;
        }

        public void SetClassification(
            ICollection<string> candidateIds,
            ClassificationLayer layer,
            CandidateClassification classification,
            string aiReviewVersion = "",
            string aiReviewFingerprint = "")
        {
            if (candidateIds == null || candidateIds.Count == 0) return;
            int shift = (int)layer * 2;
            int mask = 3 << shift;
            int value = (int)classification << shift;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowCandidatesTable, CandidateIdColumn, candidateIds, transaction);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"UPDATE Candidates SET
                        classification_flags=((classification_flags & @keep_mask) | @value),
                        ai_review_version=CASE WHEN @is_ai=1 THEN @ai_version ELSE ai_review_version END,
                        ai_review_prompt_version=CASE WHEN @is_ai=1 THEN '' ELSE ai_review_prompt_version END,
                        ai_review_fingerprint=CASE WHEN @is_ai=1 THEN @ai_fingerprint ELSE ai_review_fingerprint END,
                        ai_review_reason=CASE WHEN @is_ai=1 THEN '' ELSE ai_review_reason END,
                        manual_updated_utc=CASE WHEN @is_manual=1 AND @classification=0 THEN ''
                            WHEN @is_manual=1 THEN @updated ELSE manual_updated_utc END,
                        updated_utc=@updated
                        WHERE candidate_id IN (SELECT candidate_id FROM SelectedWorkflowCandidates);";
                    Add(command, "@keep_mask", 255 ^ mask);
                    Add(command, "@value", value);
                    Add(command, "@is_ai", layer == ClassificationLayer.AiReview ? 1 : 0);
                    Add(command, "@is_manual", layer == ClassificationLayer.Manual ? 1 : 0);
                    Add(command, "@classification", (int)classification);
                    Add(command, "@ai_version", aiReviewVersion);
                    Add(command, "@ai_fingerprint", aiReviewFingerprint);
                    Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SetAiReviewResults(ICollection<AiClassificationUpdate> updates)
        {
            if (updates == null || updates.Count == 0) return;
            const int shift = (int)ClassificationLayer.AiReview * 2;
            const int mask = 3 << shift;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                foreach (AiClassificationUpdate update in updates)
                {
                    if (update == null || string.IsNullOrWhiteSpace(update.CandidateId)) continue;
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"UPDATE Candidates SET
                            classification_flags=((classification_flags & @keep_mask) | @value),
                            ai_review_version=@version,
                            ai_review_prompt_version=@prompt_version,
                            ai_review_fingerprint=@fingerprint,
                            ai_review_reason=@reason,
                            updated_utc=@updated
                            WHERE candidate_id=@id;";
                        Add(command, "@keep_mask", 255 ^ mask);
                        Add(command, "@value", (int)update.Classification << shift);
                        Add(command, "@version", update.ReviewVersion);
                        Add(command, "@prompt_version", update.PromptVersion);
                        Add(command, "@fingerprint", update.ReviewFingerprint);
                        Add(command, "@reason", LimitText(update.Reason, 500));
                        Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                        Add(command, "@id", update.CandidateId);
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        public void RestoreLocalClassification(ICollection<string> candidateIds)
        {
            if (candidateIds == null || candidateIds.Count == 0) return;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowCandidatesTable, CandidateIdColumn, candidateIds, transaction);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"UPDATE Candidates SET
                        classification_flags=(classification_flags & 15),
                        ai_review_version='', ai_review_prompt_version='', ai_review_fingerprint='', ai_review_reason='',
                        manual_updated_utc='', updated_utc=@updated
                        WHERE candidate_id IN (SELECT candidate_id FROM SelectedWorkflowCandidates);";
                    Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SaveTranslation(
            string candidateId,
            string targetLanguage,
            string translationText,
            TranslationOrigin origin,
            string relativePath,
            string entryKey)
        {
            SaveTranslationCore(candidateId, targetLanguage, translationText, origin, relativePath, entryKey, false);
        }

        public bool TrySaveHistoricalTranslation(string candidateId, string targetLanguage,
            string translationText, string relativePath, string entryKey)
        {
            return SaveTranslationCore(candidateId, targetLanguage, translationText,
                TranslationOrigin.AiTranslation, relativePath, entryKey, true);
        }

        private bool SaveTranslationCore(string candidateId, string targetLanguage, string translationText,
            TranslationOrigin origin, string relativePath, string entryKey, bool onlyFillMissing)
        {
            string translationHash = WorkflowIdentity.HashText(translationText);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO TranslationResults
                    (candidate_id, target_language, translation_state, translation_origin, translation_text,
                     translation_hash, source_text_hash_at_translation, managed_output_file, managed_output_key,
                     validation_status, last_sync_status, last_sync_error, last_synced_utc, updated_utc)
                    SELECT candidate_id, @target, @state, @origin, @text, @hash, source_text_hash, @path, @key,
                           'Valid', 'Synced', '', @updated, @updated
                    FROM Candidates WHERE candidate_id=@id
                    ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                    translation_state=excluded.translation_state, translation_origin=excluded.translation_origin,
                    translation_text=excluded.translation_text, translation_hash=excluded.translation_hash,
                    source_text_hash_at_translation=excluded.source_text_hash_at_translation,
                    managed_output_file=excluded.managed_output_file, managed_output_key=excluded.managed_output_key,
                    validation_status='Valid', last_sync_status='Synced', last_sync_error='',
                    last_synced_utc=excluded.last_synced_utc, error_text='',
                    ai_provider='', ai_model='', ai_prompt_version='', ai_run_id='', ai_batch_index=0,
                    updated_utc=excluded.updated_utc
                    WHERE excluded.translation_origin >= TranslationResults.translation_origin
                      AND (@fill_missing=0 OR COALESCE(TranslationResults.translation_text,'')='');";
                Add(command, "@fill_missing", onlyFillMissing ? 1 : 0);
                Add(command, "@target", targetLanguage);
                Add(command, "@state", (int)CandidateTranslationState.Translated);
                Add(command, "@origin", (int)origin);
                Add(command, "@text", translationText);
                Add(command, "@hash", translationHash);
                Add(command, "@path", relativePath);
                Add(command, "@key", entryKey);
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                Add(command, "@id", candidateId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        public void SetTranslationState(
            ICollection<string> candidateIds,
            string targetLanguage,
            CandidateTranslationState state,
            string errorText = "")
        {
            if (candidateIds == null || candidateIds.Count == 0) return;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                CreateAndFillTemporarySelection(
                    connection, SelectedWorkflowCandidatesTable, CandidateIdColumn, candidateIds, transaction);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT INTO TranslationResults
                        (candidate_id, target_language, translation_state, error_text, updated_utc)
                        SELECT Candidates.candidate_id, @target, @state, @error, @updated
                        FROM Candidates INNER JOIN SelectedWorkflowCandidates selected
                            ON selected.candidate_id=Candidates.candidate_id
                        WHERE 1=1
                        ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                        translation_state=excluded.translation_state, error_text=excluded.error_text,
                        updated_utc=excluded.updated_utc;";
                    Add(command, "@target", targetLanguage);
                    Add(command, "@state", (int)state);
                    Add(command, "@error", errorText ?? string.Empty);
                    Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SetTranslationValidationFailure(
            string candidateId,
            string targetLanguage,
            string errorText,
            string rejectedOutput = "",
            string aiProvider = "",
            string aiModel = "",
            string aiPromptVersion = "",
            string aiRunId = "",
            int aiBatchIndex = 0)
        {
            string storedError = JsonConvert.SerializeObject(new
            {
                format = "atc-translation-failure-v1",
                reason = LimitText(errorText ?? string.Empty, 220),
                rejectedOutput = LimitText(rejectedOutput ?? string.Empty, 350)
            });
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO TranslationResults
                    (candidate_id, target_language, translation_state, error_text,
                     validation_status, ai_provider, ai_model, ai_prompt_version,
                     ai_run_id, ai_batch_index, updated_utc)
                    VALUES (@id, @target, @failed, @error, 'Invalid', @provider, @model,
                            @prompt_version, @run_id, @batch_index, @updated)
                    ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                    translation_state=excluded.translation_state,
                    error_text=excluded.error_text,
                    validation_status='Invalid',
                    ai_provider=excluded.ai_provider,
                    ai_model=excluded.ai_model,
                    ai_prompt_version=excluded.ai_prompt_version,
                    ai_run_id=excluded.ai_run_id,
                    ai_batch_index=excluded.ai_batch_index,
                    updated_utc=excluded.updated_utc;";
                Add(command, "@id", candidateId);
                Add(command, "@target", targetLanguage);
                Add(command, "@failed", (int)CandidateTranslationState.Failed);
                Add(command, "@error", storedError);
                Add(command, "@provider", aiProvider ?? string.Empty);
                Add(command, "@model", aiModel ?? string.Empty);
                Add(command, "@prompt_version", aiPromptVersion ?? string.Empty);
                Add(command, "@run_id", aiRunId ?? string.Empty);
                Add(command, "@batch_index", Math.Max(0, aiBatchIndex));
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                command.ExecuteNonQuery();
            }
        }

        public void SaveExternalTranslation(
            string candidateId,
            string targetLanguage,
            string translationText,
            TranslationOrigin origin,
            string sourcePackageId,
            string sourceFileRelativePath,
            string sourceEntryKey)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO TranslationResults
                    (candidate_id, target_language, translation_state, translation_origin, translation_text,
                     translation_hash, source_text_hash_at_translation, source_file_relative_path,
                     source_entry_key, source_package_id, validation_status, last_sync_status,
                     last_sync_error, last_synced_utc, updated_utc)
                    SELECT candidate_id, @target, @state, @origin, @text, @hash, source_text_hash, @source_file,
                           @source_key, @source_package, 'Valid', 'Synced', '', @updated, @updated
                           FROM Candidates WHERE candidate_id=@id
                    ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                    translation_state=excluded.translation_state, translation_origin=excluded.translation_origin,
                    translation_text=excluded.translation_text, translation_hash=excluded.translation_hash,
                    source_text_hash_at_translation=excluded.source_text_hash_at_translation,
                    source_file_relative_path=excluded.source_file_relative_path,
                    source_entry_key=excluded.source_entry_key,
                    source_package_id=excluded.source_package_id,
                    validation_status='Valid',
                    last_sync_status=CASE WHEN TranslationResults.managed_output_file<>''
                        THEN 'SourceChanged' ELSE 'Synced' END,
                    last_sync_error='', last_synced_utc=excluded.last_synced_utc,
                    error_text='', ai_provider='', ai_model='', ai_prompt_version='',
                    ai_run_id='', ai_batch_index=0, updated_utc=excluded.updated_utc
                    WHERE excluded.translation_origin >= TranslationResults.translation_origin;";
                Add(command, "@target", targetLanguage);
                Add(command, "@state", (int)CandidateTranslationState.Translated);
                Add(command, "@origin", (int)origin);
                Add(command, "@text", translationText);
                Add(command, "@hash", WorkflowIdentity.HashText(translationText));
                Add(command, "@source_file", sourceFileRelativePath);
                Add(command, "@source_key", sourceEntryKey);
                Add(command, "@source_package", sourcePackageId);
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                Add(command, "@id", candidateId);
                command.ExecuteNonQuery();
            }
        }

        public void ResetExternalTranslation(
            string candidateId,
            string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE TranslationResults SET
                    translation_state=0, translation_origin=0, translation_text='', translation_hash='',
                    source_text_hash_at_translation='', source_package_id='',
                    source_file_relative_path='', source_entry_key='', validation_status='',
                    ai_provider='', ai_model='', ai_prompt_version='', ai_run_id='', ai_batch_index=0,
                    last_sync_status='SourceMissing',
                    last_sync_error='External translation source changed', updated_utc=@updated
                    WHERE candidate_id=@candidate AND target_language=@target
                      AND translation_origin IN (@native, @third_party);";
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                Add(command, "@candidate", candidateId);
                Add(command, "@target", targetLanguage);
                Add(command, "@native", (int)TranslationOrigin.ModNative);
                Add(command, "@third_party", (int)TranslationOrigin.ThirdParty);
                command.ExecuteNonQuery();
            }
        }

        public void RemoveMissingExternalTranslations(
            string modIdentity,
            string targetLanguage,
            ICollection<string> detectedCandidateIds)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                CreateAndFillTemporarySelection(
                    connection,
                    CurrentExternalTranslationsTable,
                    CandidateIdColumn,
                    detectedCandidateIds ?? Array.Empty<string>(),
                    transaction);
                CreateAndFillTemporarySelection(connection, CurrentLoadedNativeSourcesTable,
                    ModIdentityColumn, _loadedNativeSources, transaction);
                using (DbCommand markMissing = connection.CreateCommand())
                {
                    markMissing.Transaction = transaction;
                    markMissing.CommandText = @"UPDATE TranslationResults SET
                        translation_state=0, translation_origin=0,
                        translation_text='', translation_hash='', source_text_hash_at_translation='',
                        source_package_id='', source_file_relative_path='', source_entry_key='',
                        validation_status='', error_text='', last_sync_status='SourceMissing',
                        last_sync_error='External translation source is no longer present',
                        updated_utc=@updated
                        WHERE target_language=@target
                        AND translation_origin IN (@native, @third_party)
                        AND candidate_id IN (
                            SELECT Candidates.candidate_id FROM Candidates
                            INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                            WHERE Candidates.mod_identity=@mod
                              AND Candidates.source_domain=@xml_domain
                              AND Candidates.is_present=1
                              AND " + GetCurrentAnalyzerCandidateSql() + @")
                        AND NOT EXISTS (
                            SELECT 1 FROM NativeTranslationScans scan
                            JOIN Mods provider ON provider.mod_identity=scan.mod_identity
                            JOIN " + CurrentLoadedNativeSourcesTable + @" loaded
                              ON loaded.mod_identity=provider.mod_identity
                            WHERE scan.target_language=@target AND scan.state IN ('Failed','Partial')
                              AND lower(provider.package_id)=lower(TranslationResults.source_package_id))
                        AND NOT EXISTS (SELECT 1 FROM " + CurrentExternalTranslationsTable + @" current
                            WHERE current.candidate_id=TranslationResults.candidate_id);";
                    Add(markMissing, "@updated", ToDbTime(DateTime.UtcNow));
                    Add(markMissing, "@target", targetLanguage);
                    Add(markMissing, "@target_language", targetLanguage);
                    Add(markMissing, "@native", (int)TranslationOrigin.ModNative);
                    Add(markMissing, "@third_party", (int)TranslationOrigin.ThirdParty);
                    Add(markMissing, "@mod", modIdentity);
                    Add(markMissing, "@xml_domain", (int)CandidateSourceDomain.Xml);
                    Add(markMissing, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                    Add(markMissing, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                    markMissing.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void BindObservedGeneratedTranslations(string modIdentity, string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT OR IGNORE INTO TranslationResults
                    (candidate_id, target_language, translation_state, translation_origin, translation_text,
                     translation_hash, source_text_hash_at_translation, managed_output_file,
                     managed_output_key, updated_utc)
                    SELECT Candidates.candidate_id, @target, @translated, @local_ai,
                           TranslationFileEntries.observed_text, TranslationFileEntries.observed_hash,
                           Candidates.source_text_hash, Candidates.output_relative_path,
                           Candidates.output_entry_key, @updated
                    FROM Candidates INNER JOIN TranslationFileEntries
                      ON TranslationFileEntries.relative_path=Candidates.output_relative_path
                     AND TranslationFileEntries.entry_key=Candidates.output_entry_key
                     AND TranslationFileEntries.target_language=@target
                    INNER JOIN Mods ON Mods.mod_identity=Candidates.mod_identity
                    WHERE Candidates.mod_identity=@mod
                      AND Candidates.is_present=1
                      AND " + GetCurrentAnalyzerCandidateSql() + @"
                      AND Candidates.output_relative_path<>'' AND Candidates.output_entry_key<>'';";
                Add(command, "@target", targetLanguage);
                Add(command, "@target_language", targetLanguage);
                Add(command, "@translated", (int)CandidateTranslationState.Translated);
                Add(command, "@local_ai", (int)TranslationOrigin.AiTranslation);
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                Add(command, "@mod", modIdentity);
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                command.ExecuteNonQuery();
            }
        }

        public Guid CreatePendingFileOperation(
            string candidateId,
            string targetLanguage,
            int operationKind,
            TranslationOrigin translationOrigin,
            string relativePath,
            string entryKey,
            string desiredText)
        {
            PendingFileOperationRecord operation = new PendingFileOperationRecord
            {
                CandidateId = candidateId,
                TargetLanguage = targetLanguage,
                OperationKind = operationKind,
                TranslationOrigin = translationOrigin,
                RelativePath = relativePath,
                EntryKey = entryKey,
                DesiredText = desiredText
            };
            CreatePendingFileOperations(new[] { operation });
            return operation.OperationId;
        }

        public void CreatePendingFileOperations(IList<PendingFileOperationRecord> operations)
        {
            if (operations == null || operations.Count == 0) return;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                foreach (PendingFileOperationRecord operation in operations)
                {
                    if (operation == null) continue;
                    if (operation.OperationId == Guid.Empty) operation.OperationId = Guid.NewGuid();
                    operation.DesiredHash = WorkflowIdentity.HashText(operation.DesiredText);
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT INTO PendingFileOperations
                            (operation_id, candidate_id, target_language, operation_kind,
                             translation_origin, relative_path, entry_key, desired_text,
                             desired_hash, source_text_hash, ai_provider, ai_model,
                             ai_prompt_version, ai_run_id, ai_batch_index,
                             state, created_utc, error_text, reuse_provenance_json)
                            SELECT @operation, candidate_id, @target, @kind, @origin, @path, @key,
                                   @text, @hash, source_text_hash, @ai_provider, @ai_model,
                                   @ai_prompt_version, @ai_run_id, @ai_batch_index, 0, @created, '', @reuse
                            FROM Candidates WHERE candidate_id=@candidate;";
                        Add(command, "@operation", operation.OperationId.ToString("N"));
                        Add(command, "@candidate", operation.CandidateId);
                        Add(command, "@target", operation.TargetLanguage);
                        Add(command, "@kind", operation.OperationKind);
                        Add(command, "@origin", (int)operation.TranslationOrigin);
                        Add(command, "@path", operation.RelativePath);
                        Add(command, "@key", operation.EntryKey);
                        Add(command, "@text", operation.DesiredText);
                        Add(command, "@hash", operation.DesiredHash);
                        Add(command, "@ai_provider", operation.AiProvider);
                        Add(command, "@ai_model", operation.AiModel);
                        Add(command, "@ai_prompt_version", operation.AiPromptVersion);
                        Add(command, "@ai_run_id", operation.AiRunId);
                        Add(command, "@ai_batch_index", operation.AiBatchIndex);
                        Add(command, "@created", ToDbTime(DateTime.UtcNow));
                        Add(command, "@reuse", operation.ReuseProvenanceJson);
                        if (command.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException(
                                "Cannot create a pending file operation for a missing candidate: " +
                                operation.CandidateId);
                    }
                }
                transaction.Commit();
            }
        }

        public List<PendingFileOperationRecord> GetPendingFileOperations(string targetLanguage)
        {
            List<PendingFileOperationRecord> result = new List<PendingFileOperationRecord>();
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT * FROM PendingFileOperations
                    WHERE state IN (0,2) AND target_language=@target ORDER BY created_utc;";
                Add(command, "@target", targetLanguage);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new PendingFileOperationRecord
                        {
                            OperationId = Guid.ParseExact(Convert.ToString(reader["operation_id"]), "N"),
                            CandidateId = Convert.ToString(reader["candidate_id"]),
                            TargetLanguage = Convert.ToString(reader["target_language"]),
                            OperationKind = Convert.ToInt32(reader["operation_kind"]),
                            TranslationOrigin = (TranslationOrigin)Convert.ToInt32(reader["translation_origin"]),
                            RelativePath = Convert.ToString(reader["relative_path"]),
                            EntryKey = Convert.ToString(reader["entry_key"]),
                            DesiredText = Convert.ToString(reader["desired_text"]),
                            DesiredHash = Convert.ToString(reader["desired_hash"]),
                            SourceTextHash = Convert.ToString(reader["source_text_hash"]),
                            AiProvider = Convert.ToString(reader["ai_provider"]),
                            AiModel = Convert.ToString(reader["ai_model"]),
                            AiPromptVersion = Convert.ToString(reader["ai_prompt_version"]),
                            AiRunId = Convert.ToString(reader["ai_run_id"]),
                            AiBatchIndex = Convert.ToInt32(reader["ai_batch_index"])
                        });
                    }
                }
            }
            return result;
        }

        public void CompletePendingTranslation(
            Guid operationId,
            string candidateId,
            string targetLanguage,
            string translationText,
            TranslationOrigin origin,
            string relativePath,
            string entryKey,
            bool markManualClassification = false,
            string aiProvider = "",
            string aiModel = "",
            string aiPromptVersion = "",
            string aiRunId = "",
            int aiBatchIndex = 0)
        {
            CompletePendingTranslations(new[]
            {
                new PendingTranslationCompletion
                {
                    OperationId = operationId,
                    CandidateId = candidateId,
                    TargetLanguage = targetLanguage,
                    TranslationText = translationText,
                    Origin = origin,
                    RelativePath = relativePath,
                    EntryKey = entryKey,
                    MarkManualClassification = markManualClassification,
                    AiProvider = aiProvider ?? string.Empty,
                    AiModel = aiModel ?? string.Empty,
                    AiPromptVersion = aiPromptVersion ?? string.Empty,
                    AiRunId = aiRunId ?? string.Empty,
                    AiBatchIndex = aiBatchIndex
                }
            });
        }

        public void CompletePendingTranslations(
            ICollection<PendingTranslationCompletion> completions)
        {
            if (completions == null || completions.Count == 0) return;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                foreach (PendingTranslationCompletion completion in completions)
                {
                    if (completion == null) continue;
                    string now = ToDbTime(DateTime.UtcNow);
                    using (DbCommand candidate = connection.CreateCommand())
                    {
                        candidate.Transaction = transaction;
                        candidate.CommandText = @"INSERT INTO TranslationResults
                            (candidate_id, target_language, translation_state, translation_origin, translation_text,
                             translation_hash, source_text_hash_at_translation, managed_output_file, managed_output_key,
                             validation_status, last_sync_status, last_sync_error, last_synced_utc,
                             ai_provider, ai_model, ai_prompt_version, ai_run_id, ai_batch_index, updated_utc)
                            SELECT candidate_id, @target, @state, @origin, @text, @hash, source_text_hash, @path, @key,
                                   'Valid', 'Synced', '', @updated, @ai_provider, @ai_model,
                                   @ai_prompt_version, @ai_run_id, @ai_batch_index, @updated
                            FROM Candidates WHERE candidate_id=@id
                            ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                            translation_state=excluded.translation_state, translation_origin=excluded.translation_origin,
                            translation_text=excluded.translation_text, translation_hash=excluded.translation_hash,
                            source_text_hash_at_translation=excluded.source_text_hash_at_translation,
                            managed_output_file=excluded.managed_output_file, managed_output_key=excluded.managed_output_key,
                            validation_status='Valid', last_sync_status='Synced', last_sync_error='',
                            last_synced_utc=excluded.last_synced_utc, error_text='',
                            ai_provider=excluded.ai_provider, ai_model=excluded.ai_model,
                            ai_prompt_version=excluded.ai_prompt_version, ai_run_id=excluded.ai_run_id,
                            ai_batch_index=excluded.ai_batch_index, updated_utc=excluded.updated_utc
                            WHERE excluded.translation_origin >= TranslationResults.translation_origin;";
                        Add(candidate, "@target", completion.TargetLanguage);
                        Add(candidate, "@state", (int)CandidateTranslationState.Translated);
                        Add(candidate, "@origin", (int)completion.Origin);
                        Add(candidate, "@text", completion.TranslationText);
                        Add(candidate, "@hash", WorkflowIdentity.HashText(completion.TranslationText));
                        Add(candidate, "@path", completion.RelativePath);
                        Add(candidate, "@key", completion.EntryKey);
                        Add(candidate, "@ai_provider", completion.AiProvider);
                        Add(candidate, "@ai_model", completion.AiModel);
                        Add(candidate, "@ai_prompt_version", completion.AiPromptVersion);
                        Add(candidate, "@ai_run_id", completion.AiRunId);
                        Add(candidate, "@ai_batch_index", completion.AiBatchIndex);
                        Add(candidate, "@updated", now);
                        Add(candidate, "@id", completion.CandidateId);
                        candidate.ExecuteNonQuery();
                    }
                    if (completion.MarkManualClassification)
                    {
                        using (DbCommand classification = connection.CreateCommand())
                        {
                            classification.Transaction = transaction;
                            int shift = (int)ClassificationLayer.Manual * 2;
                            int mask = 3 << shift;
                            classification.CommandText = @"UPDATE Candidates SET
                                classification_flags=((classification_flags & @keep_mask) | @value),
                                manual_updated_utc=@updated,
                                updated_utc=@updated WHERE candidate_id=@id;";
                            Add(classification, "@keep_mask", 255 ^ mask);
                            Add(classification, "@value",
                                (int)CandidateClassification.NeedsTranslation << shift);
                            Add(classification, "@updated", now);
                            Add(classification, "@id", completion.CandidateId);
                            classification.ExecuteNonQuery();
                        }
                    }
                    RestoreReuseProvenance(connection, transaction, completion);
                    using (DbCommand operation = connection.CreateCommand())
                    {
                        operation.Transaction = transaction;
                        operation.CommandText = @"UPDATE PendingFileOperations SET state=1,
                            completed_utc=@completed WHERE operation_id=@operation;";
                        Add(operation, "@completed", now);
                        Add(operation, "@operation", completion.OperationId.ToString("N"));
                        operation.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        public void FailPendingFileOperation(Guid operationId, Exception error)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE PendingFileOperations SET state=2, completed_utc=@completed,
                    error_text=@error WHERE operation_id=@operation;";
                Add(command, "@completed", ToDbTime(DateTime.UtcNow));
                Add(command, "@error", error?.Message ?? string.Empty);
                Add(command, "@operation", operationId.ToString("N"));
                command.ExecuteNonQuery();
            }
        }

        public void AbandonPendingFileOperation(Guid operationId, string reason)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE PendingFileOperations SET state=3,
                    completed_utc=@completed, error_text=@error
                    WHERE operation_id=@operation;";
                Add(command, "@completed", ToDbTime(DateTime.UtcNow));
                Add(command, "@error", LimitText(reason, 1000));
                Add(command, "@operation", operationId.ToString("N"));
                command.ExecuteNonQuery();
            }
        }

        public void CompletePendingDeletion(Guid operationId, string candidateId, string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                using (DbCommand translation = connection.CreateCommand())
                {
                    translation.Transaction = transaction;
                    translation.CommandText = @"INSERT INTO TranslationResults
                        (candidate_id, target_language, translation_state, translation_origin,
                         translation_text, translation_hash, source_text_hash_at_translation,
                         source_package_id, source_file_relative_path, source_entry_key,
                         managed_output_file, managed_output_key, error_text, validation_status,
                         last_sync_status, last_sync_error, last_synced_utc, updated_utc)
                        VALUES (@candidate, @target, 0, 0, '', '', '', '', '', '', '', '', '', '',
                                'Synced', '', @updated, @updated)
                        ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                        translation_state=0, translation_origin=0, translation_text='', translation_hash='',
                        source_text_hash_at_translation='', source_package_id='', source_file_relative_path='',
                        source_entry_key='', managed_output_file='', managed_output_key='', error_text='',
                        validation_status='', last_sync_status='Synced', last_sync_error='',
                        ai_provider='', ai_model='', ai_prompt_version='', ai_run_id='', ai_batch_index=0,
                        last_synced_utc=excluded.last_synced_utc, updated_utc=excluded.updated_utc;";
                    Add(translation, "@candidate", candidateId);
                    Add(translation, "@target", targetLanguage);
                    Add(translation, "@updated", ToDbTime(DateTime.UtcNow));
                    translation.ExecuteNonQuery();
                }
                using (DbCommand operation = connection.CreateCommand())
                {
                    operation.Transaction = transaction;
                    operation.CommandText = @"UPDATE PendingFileOperations SET state=1, completed_utc=@completed
                        WHERE operation_id=@operation;";
                    Add(operation, "@completed", ToDbTime(DateTime.UtcNow));
                    Add(operation, "@operation", operationId.ToString("N"));
                    operation.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void SaveDryRunReport(Guid runId, string modIdentity, string reportJson)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT OR REPLACE INTO DryRunReports
                    (run_id, mod_identity, report_json, created_utc)
                    VALUES (@run, @mod, @report, @created);";
                Add(command, "@run", runId.ToString("N"));
                Add(command, "@mod", modIdentity);
                Add(command, "@report", reportJson);
                Add(command, "@created", ToDbTime(DateTime.UtcNow));
                command.ExecuteNonQuery();
            }
        }

        public string GetLatestAggregateDryRunReportJson()
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT report_json FROM DryRunReports
                    WHERE mod_identity='__aggregate__' ORDER BY created_utc DESC LIMIT 1;";
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value);
            }
        }

        public void SaveWorkflowModReports(
            Guid runId,
            WorkflowTaskKind taskKind,
            IDictionary<string, string> reports)
        {
            if (reports == null || reports.Count == 0) return;
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                foreach (KeyValuePair<string, string> report in reports)
                {
                    if (string.IsNullOrWhiteSpace(report.Key)) continue;
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT OR REPLACE INTO WorkflowModReports
                            (run_id, mod_identity, task_kind, report_json, created_utc)
                            VALUES (@run, @mod, @kind, @report, @created);";
                        Add(command, "@run", runId.ToString("N"));
                        Add(command, "@mod", report.Key);
                        Add(command, "@kind", (int)taskKind);
                        Add(command, "@report", report.Value ?? string.Empty);
                        Add(command, "@created", ToDbTime(DateTime.UtcNow));
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        public void StartWorkflowRun(Guid runId, WorkflowTaskKind kind, string inputJson = "")
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO WorkflowRuns
                    (run_id, task_kind, state, input_json, started_utc)
                    VALUES (@run, @kind, @state, @input, @started);";
                Add(command, "@run", runId.ToString("N"));
                Add(command, "@kind", (int)kind);
                Add(command, "@state", (int)WorkflowRunState.Running);
                Add(command, "@input", inputJson ?? string.Empty);
                Add(command, "@started", ToDbTime(DateTime.UtcNow));
                command.ExecuteNonQuery();
            }
        }

        public void CompleteWorkflowRun(Guid runId, WorkflowRunState state, string resultJson, string errorText)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE WorkflowRuns SET state=@state, result_json=@result,
                    error_text=@error, completed_utc=@completed WHERE run_id=@run;";
                Add(command, "@state", (int)state);
                Add(command, "@result", resultJson ?? string.Empty);
                Add(command, "@error", errorText ?? string.Empty);
                Add(command, "@completed", ToDbTime(DateTime.UtcNow));
                Add(command, "@run", runId.ToString("N"));
                command.ExecuteNonQuery();
            }
        }

        public void ClearTranslation(string candidateId, string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO TranslationResults
                    (candidate_id, target_language, translation_state, translation_origin,
                     translation_text, translation_hash, source_text_hash_at_translation,
                     source_package_id, source_file_relative_path, source_entry_key,
                     managed_output_file, managed_output_key, error_text, validation_status,
                     last_sync_status, last_sync_error, last_synced_utc, updated_utc)
                    VALUES (@id, @target, 0, 0, '', '', '', '', '', '', '', '', '', '',
                            'Synced', '', @updated, @updated)
                    ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                    translation_state=0, translation_origin=0, translation_text='', translation_hash='',
                    source_text_hash_at_translation='', source_package_id='', source_file_relative_path='',
                    source_entry_key='', managed_output_file='', managed_output_key='', error_text='',
                    validation_status='', last_sync_status='Synced', last_sync_error='',
                    last_synced_utc=excluded.last_synced_utc, updated_utc=excluded.updated_utc;";
                Add(command, "@id", candidateId);
                Add(command, "@target", targetLanguage);
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                command.ExecuteNonQuery();
            }
        }

        public void ClearTranslationIfOrigin(
            string candidateId,
            string targetLanguage,
            TranslationOrigin origin)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE TranslationResults SET
                    translation_state=0, translation_origin=0, translation_text='', translation_hash='',
                    source_text_hash_at_translation='', source_package_id='', source_file_relative_path='',
                    source_entry_key='', managed_output_file='', managed_output_key='', error_text='',
                    validation_status='', last_sync_status='Synced', last_sync_error='',
                    last_synced_utc=@updated, updated_utc=@updated
                    WHERE candidate_id=@id AND target_language=@target
                      AND translation_origin=@origin;";
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                Add(command, "@id", candidateId ?? string.Empty);
                Add(command, "@target", targetLanguage ?? string.Empty);
                Add(command, "@origin", (int)origin);
                command.ExecuteNonQuery();
            }
        }

        public void UpsertObservedTranslationEntry(
            string relativePath,
            string entryKey,
            string targetLanguage,
            string candidateId,
            string text,
            DateTime observedUtc)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO TranslationFileEntries
                    (relative_path, entry_key, target_language, candidate_id, observed_text, observed_hash, observed_utc)
                    VALUES (@path, @key, @target, @candidate, @text, @hash, @observed)
                    ON CONFLICT(relative_path, entry_key, target_language) DO UPDATE SET
                    candidate_id=COALESCE(excluded.candidate_id, TranslationFileEntries.candidate_id),
                    observed_text=excluded.observed_text, observed_hash=excluded.observed_hash,
                    observed_utc=excluded.observed_utc;";
                Add(command, "@path", relativePath);
                Add(command, "@key", entryKey);
                Add(command, "@target", targetLanguage);
                Add(command, "@candidate", string.IsNullOrEmpty(candidateId) ? (object)DBNull.Value : candidateId);
                Add(command, "@text", text);
                Add(command, "@hash", WorkflowIdentity.HashText(text));
                Add(command, "@observed", ToDbTime(observedUtc));
                command.ExecuteNonQuery();
            }
        }

        public Dictionary<string, TranslationFileCacheRecord> GetTranslationFileCache(string targetLanguage)
        {
            Dictionary<string, TranslationFileCacheRecord> result =
                new Dictionary<string, TranslationFileCacheRecord>(StringComparer.Ordinal);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT relative_path, content_hash, entries_json,
                    file_length, last_write_utc
                    FROM TranslationFileCache WHERE target_language=@target;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        TranslationFileCacheRecord record = new TranslationFileCacheRecord
                        {
                            RelativePath = Convert.ToString(reader["relative_path"]),
                            ContentHash = Convert.ToString(reader["content_hash"]),
                            EntriesJson = Convert.ToString(reader["entries_json"]),
                            FileLength = Convert.ToInt64(reader["file_length"]),
                            LastWriteUtc = ParseNullableDbTime(reader["last_write_utc"])
                        };
                        result[record.RelativePath] = record;
                    }
                }
            }
            return result;
        }

        private static void ReplaceTranslationFileCache(
            DbConnection connection,
            DbTransaction transaction,
            string targetLanguage,
            ICollection<TranslationFileCacheRecord> records,
            CancellationToken cancellationToken)
        {
            {
                using (DbCommand delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM TranslationFileCache WHERE target_language=@target;";
                    Add(delete, "@target", targetLanguage ?? string.Empty);
                    delete.ExecuteNonQuery();
                }
                foreach (TranslationFileCacheRecord record in records ?? Array.Empty<TranslationFileCacheRecord>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (DbCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"INSERT INTO TranslationFileCache
                            (relative_path, target_language, content_hash, entries_json, observed_utc,
                             file_length, last_write_utc)
                            VALUES (@path, @target, @hash, @entries, @observed, @length, @last_write);";
                        Add(insert, "@path", record.RelativePath);
                        Add(insert, "@target", targetLanguage ?? string.Empty);
                        Add(insert, "@hash", record.ContentHash);
                        Add(insert, "@entries", record.EntriesJson);
                        Add(insert, "@observed", ToDbTime(DateTime.UtcNow));
                        Add(insert, "@length", record.FileLength);
                        Add(insert, "@last_write", record.LastWriteUtc.HasValue
                            ? ToDbTime(record.LastWriteUtc.Value)
                            : string.Empty);
                        insert.ExecuteNonQuery();
                    }
                }
            }
        }

        public bool HasCompletedSynchronization(string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT 1 FROM TranslationSyncState
                    WHERE target_language=@target AND completed_generation>0 LIMIT 1;";
                Add(command, "@target", targetLanguage ?? string.Empty);
                return command.ExecuteScalar() != null;
            }
        }

        public long CommitSynchronization(
            string targetLanguage,
            ICollection<TranslationSynchronizationMutation> mutations,
            ICollection<ObservedTranslationEntry> observedEntries,
            ICollection<TranslationFileCacheRecord> fileCache,
            Func<long, string> resultJsonFactory,
            Action<int, int, string> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            targetLanguage = targetLanguage ?? string.Empty;
            string now = ToDbTime(DateTime.UtcNow);
            int mutationCount = mutations?.Count ?? 0;
            int observedCount = observedEntries?.Count ?? 0;
            int totalUnits = mutationCount + observedCount + 3;
            int completedUnits = 0;
            progress?.Invoke(completedUnits, totalUnits, "写入译文状态变更");
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                foreach (TranslationSynchronizationMutation mutation in
                         mutations ?? Array.Empty<TranslationSynchronizationMutation>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (mutation == null || string.IsNullOrWhiteSpace(mutation.CandidateId)) continue;
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        if (mutation.Kind == TranslationSynchronizationMutationKind.Invalid)
                        {
                            command.CommandText = @"INSERT INTO TranslationResults
                                (candidate_id, target_language, translation_state, translation_origin,
                                 translation_text, translation_hash, source_text_hash_at_translation,
                                 managed_output_file, managed_output_key, error_text, validation_status,
                                 last_sync_status, last_sync_error, last_synced_utc, updated_utc)
                                SELECT candidate_id, @target, @failed, @origin, @text, @hash,
                                       source_text_hash, @path, @key, @sync_error, 'Invalid',
                                       'Invalid', @sync_error, @updated, @updated
                                FROM Candidates WHERE candidate_id=@id
                                ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                                translation_state=excluded.translation_state,
                                translation_origin=excluded.translation_origin,
                                translation_text=excluded.translation_text,
                                translation_hash=excluded.translation_hash,
                                source_text_hash_at_translation=excluded.source_text_hash_at_translation,
                                managed_output_file=excluded.managed_output_file,
                                managed_output_key=excluded.managed_output_key,
                                error_text=excluded.error_text,
                                validation_status='Invalid', last_sync_status='Invalid',
                                last_sync_error=excluded.last_sync_error,
                                last_synced_utc=excluded.last_synced_utc, updated_utc=excluded.updated_utc;";
                            Add(command, "@failed", (int)CandidateTranslationState.Failed);
                            Add(command, "@origin", (int)mutation.Origin);
                            Add(command, "@text", mutation.Text ?? string.Empty);
                            Add(command, "@hash", WorkflowIdentity.HashText(mutation.Text));
                            Add(command, "@path", mutation.RelativePath ?? string.Empty);
                            Add(command, "@key", mutation.EntryKey ?? string.Empty);
                            Add(command, "@sync_error", LimitText(mutation.Error, 1000));
                            Add(command, "@updated", now);
                        }
                        else if (mutation.Kind == TranslationSynchronizationMutationKind.ReadError)
                        {
                            command.CommandText = @"INSERT INTO TranslationResults
                                (candidate_id, target_language, translation_state, translation_origin,
                                 translation_text, translation_hash, source_text_hash_at_translation,
                                 last_sync_status, last_sync_error, last_synced_utc, updated_utc)
                                VALUES (@id, @target, 0, 0, '', '', '', 'ReadError', @sync_error, @updated, @updated)
                                ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                                last_sync_status='ReadError', last_sync_error=excluded.last_sync_error,
                                last_synced_utc=excluded.last_synced_utc, updated_utc=excluded.updated_utc;";
                            Add(command, "@sync_error", LimitText(mutation.Error, 1000));
                            Add(command, "@updated", now);
                        }
                        else if (mutation.Kind == TranslationSynchronizationMutationKind.Clear)
                        {
                            command.CommandText = @"INSERT INTO TranslationResults
                                (candidate_id, target_language, translation_state, translation_origin,
                                 translation_text, translation_hash, source_text_hash_at_translation,
                                 source_package_id, source_file_relative_path, source_entry_key,
                                 managed_output_file, managed_output_key, error_text, validation_status,
                                 last_sync_status, last_sync_error, last_synced_utc, updated_utc)
                                VALUES (@id, @target, 0, 0, '', '', '', '', '', '', '', '', '', '',
                                        'Missing', '', @updated, @updated)
                                ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                                translation_state=0, translation_origin=0, translation_text='', translation_hash='',
                                source_text_hash_at_translation='', source_package_id='', source_file_relative_path='',
                                source_entry_key='', managed_output_file='', managed_output_key='', error_text='',
                                validation_status='', last_sync_status='Missing', last_sync_error='',
                                last_synced_utc=excluded.last_synced_utc, updated_utc=excluded.updated_utc;";
                            Add(command, "@updated", now);
                        }
                        else
                        {
                            command.CommandText = @"INSERT INTO TranslationResults
                                (candidate_id, target_language, translation_state, translation_origin,
                                 translation_text, translation_hash, source_text_hash_at_translation,
                                 managed_output_file, managed_output_key, error_text, validation_status,
                                 last_sync_status, last_sync_error, last_synced_utc, updated_utc)
                                SELECT candidate_id, @target, @state, @origin, @text, @hash,
                                       source_text_hash, @path, @key, '', 'Valid', 'Synced', '', @updated, @updated
                                FROM Candidates WHERE candidate_id=@id
                                ON CONFLICT(candidate_id, target_language) DO UPDATE SET
                                translation_state=excluded.translation_state,
                                translation_origin=excluded.translation_origin,
                                translation_text=excluded.translation_text,
                                translation_hash=excluded.translation_hash,
                                source_text_hash_at_translation=excluded.source_text_hash_at_translation,
                                managed_output_file=excluded.managed_output_file,
                                managed_output_key=excluded.managed_output_key,
                                validation_status='Valid', last_sync_status='Synced', last_sync_error='',
                                last_synced_utc=excluded.last_synced_utc,
                                error_text='', updated_utc=excluded.updated_utc
                                WHERE excluded.translation_origin >= TranslationResults.translation_origin;";
                            Add(command, "@state", (int)CandidateTranslationState.Translated);
                            Add(command, "@origin", (int)mutation.Origin);
                            Add(command, "@text", mutation.Text ?? string.Empty);
                            Add(command, "@hash", WorkflowIdentity.HashText(mutation.Text));
                            Add(command, "@path", mutation.RelativePath ?? string.Empty);
                            Add(command, "@key", mutation.EntryKey ?? string.Empty);
                            Add(command, "@updated", now);
                        }
                        Add(command, "@id", mutation.CandidateId);
                        Add(command, "@target", targetLanguage);
                        command.ExecuteNonQuery();
                    }
                    completedUnits++;
                    if (completedUnits % 250 == 0 || completedUnits == mutationCount)
                        progress?.Invoke(completedUnits, totalUnits, "写入译文状态变更");
                }

                using (DbCommand deleteObserved = connection.CreateCommand())
                {
                    deleteObserved.Transaction = transaction;
                    deleteObserved.CommandText =
                        "DELETE FROM TranslationFileEntries WHERE target_language=@target;";
                    Add(deleteObserved, "@target", targetLanguage);
                    deleteObserved.ExecuteNonQuery();
                }
                completedUnits++;
                progress?.Invoke(completedUnits, totalUnits, "更新本地译文文件条目索引");
                foreach (ObservedTranslationEntry observed in
                         observedEntries ?? Array.Empty<ObservedTranslationEntry>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (observed == null) continue;
                    using (DbCommand insertObserved = connection.CreateCommand())
                    {
                        insertObserved.Transaction = transaction;
                        insertObserved.CommandText = @"INSERT INTO TranslationFileEntries
                            (relative_path, entry_key, target_language, candidate_id,
                             observed_text, observed_hash, observed_utc)
                            VALUES (@path, @key, @target, @candidate, @text, @hash, @observed);";
                        Add(insertObserved, "@path", observed.RelativePath ?? string.Empty);
                        Add(insertObserved, "@key", observed.EntryKey ?? string.Empty);
                        Add(insertObserved, "@target", targetLanguage);
                        Add(insertObserved, "@candidate",
                            string.IsNullOrWhiteSpace(observed.CandidateId)
                                ? (object)DBNull.Value
                                : observed.CandidateId);
                        Add(insertObserved, "@text", observed.Text ?? string.Empty);
                        Add(insertObserved, "@hash", string.IsNullOrWhiteSpace(observed.TextHash)
                            ? WorkflowIdentity.HashText(observed.Text)
                            : observed.TextHash);
                        Add(insertObserved, "@observed", now);
                        insertObserved.ExecuteNonQuery();
                    }
                    completedUnits++;
                    if ((completedUnits - mutationCount - 1) % 250 == 0 ||
                        completedUnits == mutationCount + observedCount + 1)
                        progress?.Invoke(completedUnits, totalUnits, "更新本地译文文件条目索引");
                }

                long generation;
                using (DbCommand readGeneration = connection.CreateCommand())
                {
                    readGeneration.Transaction = transaction;
                    readGeneration.CommandText = @"SELECT completed_generation FROM TranslationSyncState
                        WHERE target_language=@target;";
                    Add(readGeneration, "@target", targetLanguage);
                    object current = readGeneration.ExecuteScalar();
                    generation = current == null || current == DBNull.Value
                        ? 1L
                        : Convert.ToInt64(current) + 1L;
                }
                completedUnits++;
                progress?.Invoke(completedUnits, totalUnits, "发布完整同步代次");
                ReplaceTranslationFileCache(connection, transaction, targetLanguage, fileCache, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using (DbCommand publish = connection.CreateCommand())
                {
                    publish.Transaction = transaction;
                    publish.CommandText = @"INSERT INTO TranslationSyncState
                        (target_language, completed_generation, completed_utc, result_json)
                        VALUES (@target, @generation, @completed, @result)
                        ON CONFLICT(target_language) DO UPDATE SET
                        completed_generation=excluded.completed_generation,
                        completed_utc=excluded.completed_utc,
                        result_json=excluded.result_json;";
                    Add(publish, "@target", targetLanguage);
                    Add(publish, "@generation", generation);
                    Add(publish, "@completed", now);
                    Add(publish, "@result", resultJsonFactory?.Invoke(generation) ?? string.Empty);
                    publish.ExecuteNonQuery();
                }
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                completedUnits++;
                progress?.Invoke(completedUnits, totalUnits, "同步代次发布完成");
                return generation;
            }
        }

        public string GetWorkflowSettingJson(string key)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT value_json FROM WorkflowSettings
                    WHERE setting_key=@key LIMIT 1;";
                Add(command, "@key", key ?? string.Empty);
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value);
            }
        }

        public void SaveWorkflowSettingJson(string key, string valueJson)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO WorkflowSettings
                    (setting_key, value_json, updated_utc) VALUES (@key, @value, @updated)
                    ON CONFLICT(setting_key) DO UPDATE SET
                    value_json=excluded.value_json, updated_utc=excluded.updated_utc;";
                Add(command, "@key", key ?? string.Empty);
                Add(command, "@value", valueJson ?? string.Empty);
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertCandidate(
            DbConnection connection,
            DbTransaction transaction,
            CandidateRecord candidate,
            ClassificationLayer layer,
            int layerMask)
        {
            using (DbCommand invalidate = connection.CreateCommand())
            {
                invalidate.Transaction = transaction;
                invalidate.CommandText = @"UPDATE TranslationResults SET
                    translation_state=0, translation_origin=0,
                    translation_text='', translation_hash='',
                    source_package_id='', source_file_relative_path='', source_entry_key='',
                    validation_status='', error_text='', last_sync_status='SourceTextChanged',
                    last_sync_error='Source text changed', updated_utc=@updated
                    WHERE candidate_id=@id AND EXISTS (
                        SELECT 1 FROM Candidates
                        WHERE candidate_id=@id AND source_text_hash<>@source_hash);";
                Add(invalidate, "@id", candidate.CandidateId);
                Add(invalidate, "@source_hash", candidate.SourceTextHash);
                Add(invalidate, "@updated", ToDbTime(DateTime.UtcNow));
                invalidate.ExecuteNonQuery();
            }
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO Candidates
                    (candidate_id, mod_identity, mod_version_fingerprint, snapshot_id, source_domain, entry_kind, logical_locator, context_json, output_relative_path, output_entry_key,
                     source_file_relative_path, source_line_number, source_line_end, source_text, source_text_hash, content_fingerprint, estimated_tokens,
                     identity_schema_version, entry_identity,
                     classification_flags, xml_analyzer_version, xml_analysis_fingerprint, dll_analyzer_version,
                     dll_analysis_fingerprint, xml_reason_code, dll_reason_code, is_present, updated_utc)
                    VALUES (@id, @mod, @mod_version, @snapshot, @domain, @kind, @locator, @context, @output_path, @output_key, @file, @line, @line_end, @source, @source_hash,
                            @content, @estimated_tokens, @identity_version, @entry_identity, @flags, @xml_version, @xml_fingerprint, @dll_version, @dll_fingerprint,
                            @xml_reason, @dll_reason, 1, @updated)
                    ON CONFLICT(candidate_id) DO UPDATE SET
                    mod_version_fingerprint=excluded.mod_version_fingerprint,
                    snapshot_id=excluded.snapshot_id,
                    source_file_relative_path=excluded.source_file_relative_path,
                    context_json=excluded.context_json,
                    output_relative_path=excluded.output_relative_path,
                    output_entry_key=excluded.output_entry_key,
                    source_line_number=excluded.source_line_number,
                    source_line_end=excluded.source_line_end,
                    source_text=excluded.source_text,
                    content_fingerprint=excluded.content_fingerprint,
                    estimated_tokens=excluded.estimated_tokens,
                    identity_schema_version=excluded.identity_schema_version,
                    entry_identity=excluded.entry_identity,
                    is_present=1,
                    classification_flags=CASE WHEN Candidates.source_text_hash=excluded.source_text_hash AND
                        ((@is_xml=1 AND Candidates.xml_analysis_fingerprint=excluded.xml_analysis_fingerprint) OR
                         (@is_xml=0 AND Candidates.dll_analysis_fingerprint=excluded.dll_analysis_fingerprint))
                        THEN ((Candidates.classification_flags & @keep_layer_mask) | (excluded.classification_flags & @layer_mask))
                        ELSE ((Candidates.classification_flags & 240) | (excluded.classification_flags & @layer_mask)) END,
                    xml_analyzer_version=CASE WHEN @is_xml=1 THEN excluded.xml_analyzer_version ELSE Candidates.xml_analyzer_version END,
                    xml_analysis_fingerprint=CASE WHEN @is_xml=1 THEN excluded.xml_analysis_fingerprint ELSE Candidates.xml_analysis_fingerprint END,
                    dll_analyzer_version=CASE WHEN @is_xml=0 THEN excluded.dll_analyzer_version ELSE Candidates.dll_analyzer_version END,
                    dll_analysis_fingerprint=CASE WHEN @is_xml=0 THEN excluded.dll_analysis_fingerprint ELSE Candidates.dll_analysis_fingerprint END,
                    xml_reason_code=CASE WHEN @is_xml=1 THEN excluded.xml_reason_code ELSE Candidates.xml_reason_code END,
                    dll_reason_code=CASE WHEN @is_xml=0 THEN excluded.dll_reason_code ELSE Candidates.dll_reason_code END,
                    source_text_hash=excluded.source_text_hash, updated_utc=excluded.updated_utc;";
                Add(command, "@id", candidate.CandidateId);
                Add(command, "@mod", candidate.ModIdentity);
                Add(command, "@mod_version", candidate.ModVersionFingerprint);
                Add(command, "@snapshot", candidate.SnapshotId);
                Add(command, "@domain", (int)candidate.SourceDomain);
                Add(command, "@kind", candidate.EntryKind);
                Add(command, "@locator", candidate.LogicalLocator);
                Add(command, "@context", candidate.ContextJson);
                Add(command, "@output_path", candidate.DefaultOutputFileRelativePath);
                Add(command, "@output_key", candidate.TranslationEntryKey);
                Add(command, "@file", candidate.SourceFileRelativePath);
                Add(command, "@line", candidate.SourceLineNumber);
                Add(command, "@line_end", candidate.SourceLineEnd);
                Add(command, "@source", candidate.SourceText);
                Add(command, "@source_hash", candidate.SourceTextHash);
                Add(command, "@content", candidate.ContentFingerprint);
                Add(command, "@estimated_tokens", candidate.EstimatedTokens > 0
                    ? candidate.EstimatedTokens
                    : ApproximateTokenEstimator.Estimate(candidate.SourceText));
                Add(command, "@identity_version",
                    string.IsNullOrWhiteSpace(candidate.IdentitySchemaVersion)
                        ? WorkflowIdentity.CandidateIdentitySchemaVersion
                        : candidate.IdentitySchemaVersion);
                Add(command, "@entry_identity",
                    string.IsNullOrWhiteSpace(candidate.EntryIdentity)
                        ? WorkflowIdentity.CreateReadableEntryIdentity(
                            candidate.SourceDomain, candidate.EntryKind, candidate.LogicalLocator)
                        : candidate.EntryIdentity);
                Add(command, "@flags", candidate.ClassificationFlags);
                Add(command, "@xml_version", candidate.XmlAnalyzerVersion);
                Add(command, "@xml_fingerprint", candidate.XmlAnalysisFingerprint);
                Add(command, "@dll_version", candidate.DllAnalyzerVersion);
                Add(command, "@dll_fingerprint", candidate.DllAnalysisFingerprint);
                Add(command, "@xml_reason", candidate.XmlReasonCode);
                Add(command, "@dll_reason", candidate.DllReasonCode);
                Add(command, "@updated", ToDbTime(candidate.UpdatedUtc == default(DateTime) ? DateTime.UtcNow : candidate.UpdatedUtc));
                Add(command, "@keep_layer_mask", 255 ^ layerMask);
                Add(command, "@layer_mask", layerMask);
                Add(command, "@is_xml", layer == ClassificationLayer.Xml ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        private static CandidateRecord ReadCandidate(DbDataReader reader)
        {
            return new CandidateRecord
            {
                CandidateId = Convert.ToString(reader["candidate_id"]),
                PackageId = Convert.ToString(reader["package_id"]),
                ModIdentity = Convert.ToString(reader["mod_identity"]),
                ModVersionFingerprint = Convert.ToString(reader["mod_version_fingerprint"]),
                SnapshotId = Convert.ToString(reader["snapshot_id"]),
                SourceDomain = (CandidateSourceDomain)Convert.ToByte(reader["source_domain"]),
                EntryKind = Convert.ToString(reader["entry_kind"]),
                LogicalLocator = Convert.ToString(reader["logical_locator"]),
                ContextJson = Convert.ToString(reader["context_json"]),
                DefaultOutputFileRelativePath = Convert.ToString(reader["output_relative_path"]),
                SourceFileRelativePath = Convert.ToString(reader["source_file_relative_path"]),
                SourceLineNumber = Convert.ToInt32(reader["source_line_number"]),
                SourceLineEnd = Convert.ToInt32(reader["source_line_end"]),
                SourceText = Convert.ToString(reader["source_text"]),
                SourceTextHash = Convert.ToString(reader["source_text_hash"]),
                ContentFingerprint = Convert.ToString(reader["content_fingerprint"]),
                IdentitySchemaVersion = Convert.ToString(reader["identity_schema_version"]),
                EntryIdentity = Convert.ToString(reader["entry_identity"]),
                EstimatedTokens = Convert.ToInt64(reader["estimated_tokens"]),
                ClassificationFlags = Convert.ToByte(reader["classification_flags"]),
                XmlAnalyzerVersion = Convert.ToString(reader["xml_analyzer_version"]),
                XmlAnalysisFingerprint = Convert.ToString(reader["xml_analysis_fingerprint"]),
                DllAnalyzerVersion = Convert.ToString(reader["dll_analyzer_version"]),
                DllAnalysisFingerprint = Convert.ToString(reader["dll_analysis_fingerprint"]),
                AiReviewVersion = Convert.ToString(reader["ai_review_version"]),
                AiReviewPromptVersion = Convert.ToString(reader["ai_review_prompt_version"]),
                AiReviewFingerprint = Convert.ToString(reader["ai_review_fingerprint"]),
                XmlReasonCode = Convert.ToString(reader["xml_reason_code"]),
                DllReasonCode = Convert.ToString(reader["dll_reason_code"]),
                AiReviewReason = Convert.ToString(reader["ai_review_reason"]),
                ManualUpdatedUtc = ParseNullableDbTime(reader["manual_updated_utc"]),
                IsPresent = Convert.ToInt32(reader["is_present"]) != 0,
                TranslationState = (CandidateTranslationState)Convert.ToByte(reader["translation_state"]),
                TranslationOrigin = (TranslationOrigin)Convert.ToByte(reader["translation_origin"]),
                TranslationText = Convert.ToString(reader["translation_text"]),
                TranslationHash = Convert.ToString(reader["translation_hash"]),
                SourceTextHashAtTranslation = Convert.ToString(reader["source_text_hash_at_translation"]),
                TranslationFileRelativePath = Convert.ToString(reader["translation_file_relative_path"]),
                TranslationEntryKey = Convert.ToString(reader["translation_entry_key"]),
                TranslationSourcePackageId = Convert.ToString(reader["translation_source_package_id"]),
                TranslationSourceFileRelativePath = Convert.ToString(reader["translation_source_file_relative_path"]),
                TranslationSourceEntryKey = Convert.ToString(reader["translation_source_entry_key"]),
                ValidationStatus = Convert.ToString(reader["validation_status"]),
                TranslationError = ReadOptionalString(reader, "translation_error"),
                LastSyncStatus = Convert.ToString(reader["last_sync_status"]),
                LastSyncError = Convert.ToString(reader["last_sync_error"]),
                LastSyncedUtc = ParseNullableDbTime(reader["last_synced_utc"]),
                UpdatedUtc = DateTime.Parse(Convert.ToString(reader["updated_utc"])).ToUniversalTime()
            };
        }

        private static string ReadOptionalString(DbDataReader reader, string columnName)
        {
            for (int index = 0; index < reader.FieldCount; index++)
                if (string.Equals(reader.GetName(index), columnName, StringComparison.OrdinalIgnoreCase))
                    return Convert.ToString(reader.GetValue(index));
            return string.Empty;
        }

        private static void CreateAndFillTemporarySelection(
            DbConnection connection,
            string tableName,
            string columnName,
            IEnumerable<string> values,
            DbTransaction transaction = null)
        {
            string safeTableName = ValidateSqlIdentifier(tableName, nameof(tableName));
            string safeColumnName = ValidateSqlIdentifier(columnName, nameof(columnName));
            using (DbCommand create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS " + safeTableName +
                                     " (" + safeColumnName + " TEXT PRIMARY KEY); DELETE FROM " + safeTableName + ";";
                create.ExecuteNonQuery();
            }
            foreach (string value in values ?? Enumerable.Empty<string>())
            {
                using (DbCommand insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO " + safeTableName +
                                         "(" + safeColumnName + ") VALUES (@value);";
                    Add(insert, "@value", value ?? string.Empty);
                    insert.ExecuteNonQuery();
                }
            }
        }

        private ExpiredWorkflowDataSummary ReadExpiredDataSummary(DbConnection connection)
        {
            return new ExpiredWorkflowDataSummary
            {
                CandidateCount = CountRows(connection,
                    "SELECT COUNT(*) FROM Candidates WHERE is_present=0;"),
                TranslationResultCount = CountRows(connection,
                    @"SELECT COUNT(*) FROM TranslationResults
                      WHERE candidate_id IN (SELECT candidate_id FROM Candidates WHERE is_present=0);"),
                TranslationFileEntryCount = CountRows(connection,
                    @"SELECT COUNT(*) FROM TranslationFileEntries
                      WHERE candidate_id IN (SELECT candidate_id FROM Candidates WHERE is_present=0);"),
                PendingFileOperationCount = CountRows(connection,
                    @"SELECT COUNT(*) FROM PendingFileOperations
                      WHERE candidate_id IN (SELECT candidate_id FROM Candidates WHERE is_present=0);"),
                DatabaseBytesBefore = GetDatabaseFileLength()
            };
        }

        private long GetDatabaseFileLength()
        {
            return File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0L;
        }

        private static long CountRows(DbConnection connection, string sql)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static string ValidateSqlIdentifier(string identifier, string parameterName)
        {
            if (string.IsNullOrEmpty(identifier) || !IsAsciiIdentifierStart(identifier[0]))
                throw new ArgumentException("SQL identifier must start with an ASCII letter or underscore.", parameterName);
            for (int i = 1; i < identifier.Length; i++)
            {
                char value = identifier[i];
                if (!IsAsciiIdentifierStart(value) && (value < '0' || value > '9'))
                    throw new ArgumentException(
                        "SQL identifier may contain only ASCII letters, digits, and underscores.", parameterName);
            }
            return identifier;
        }

        private static bool IsAsciiIdentifierStart(char value)
        {
            return value == '_' || value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';
        }

        private static void Execute(DbConnection connection, DbTransaction transaction, string sql)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static void Add(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static string ToDbTime(DateTime value)
        {
            if (value == default(DateTime)) value = DateTime.UtcNow;
            return value.ToUniversalTime().ToString("o");
        }

        private static string LimitText(string value, int maximumLength)
        {
            value = value ?? string.Empty;
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }

        private static DateTime? ParseNullableDbTime(object value)
        {
            string text = value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTime.TryParse(text, out DateTime parsed)
                ? parsed.ToUniversalTime()
                : (DateTime?)null;
        }

        private static bool IsConfirmedCorruption(Exception ex)
        {
            if (ex is ConfirmedDatabaseCorruptionException) return true;
            Exception current = ex;
            while (current != null)
            {
                string message = current.Message ?? string.Empty;
                if (message.IndexOf("database disk image is malformed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("file is not a database", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("SQLITE_CORRUPT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("SQLITE_NOTADB", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                current = current.InnerException;
            }
            return false;
        }

        private static void InvalidateObsoleteAnalyzerCandidates(DbConnection connection)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE Candidates SET is_present=0, updated_utc=@updated
                    WHERE is_present<>0 AND
                         ((source_domain=0 AND
                              (xml_analyzer_version IS NULL OR xml_analyzer_version<>@xml_version))
                       OR (source_domain=1 AND
                              (dll_analyzer_version IS NULL OR dll_analyzer_version<>@dll_version)));";
                Add(command, "@updated", ToDbTime(DateTime.UtcNow));
                Add(command, "@xml_version", WorkflowIdentity.XmlAnalyzerVersion);
                Add(command, "@dll_version", WorkflowIdentity.DllAnalyzerVersion);
                command.ExecuteNonQuery();
            }
        }

        private sealed class ConfirmedDatabaseCorruptionException : Exception
        {
            public ConfirmedDatabaseCorruptionException(string message) : base(message) { }
        }
    }
}
