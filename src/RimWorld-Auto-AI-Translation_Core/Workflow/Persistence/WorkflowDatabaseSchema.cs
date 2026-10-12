using System;
using System.Collections.Generic;
using System.Data.Common;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal static class WorkflowDatabaseSchema
    {
        public const int CurrentVersion = 20;

        private static readonly IReadOnlyDictionary<int, string[]> Migrations =
            new Dictionary<int, string[]>
            {
                [1] = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS Mods (
                        mod_identity TEXT PRIMARY KEY,
                        package_id TEXT NOT NULL,
                        display_name TEXT NOT NULL,
                        root_path TEXT NOT NULL,
                        version_label TEXT NOT NULL,
                        version_fingerprint TEXT NOT NULL,
                        last_observed_utc TEXT NOT NULL);",
                    @"CREATE INDEX IF NOT EXISTS IX_Mods_PackageId ON Mods(package_id);",
                    @"CREATE TABLE IF NOT EXISTS ModFiles (
                        mod_identity TEXT NOT NULL,
                        mod_version_fingerprint TEXT NOT NULL,
                        relative_path TEXT NOT NULL,
                        file_hash TEXT NOT NULL,
                        file_length INTEGER NOT NULL,
                        line_count INTEGER NOT NULL,
                        estimated_tokens INTEGER NOT NULL,
                        PRIMARY KEY(mod_identity, mod_version_fingerprint, relative_path),
                        FOREIGN KEY(mod_identity) REFERENCES Mods(mod_identity) ON DELETE CASCADE);",
                    @"CREATE TABLE IF NOT EXISTS Candidates (
                        candidate_id TEXT PRIMARY KEY,
                        mod_identity TEXT NOT NULL,
                        mod_version_fingerprint TEXT NOT NULL,
                        source_domain INTEGER NOT NULL,
                        entry_kind TEXT NOT NULL,
                        logical_locator TEXT NOT NULL,
                        context_json TEXT NOT NULL DEFAULT '',
                        output_relative_path TEXT NOT NULL DEFAULT '',
                        output_entry_key TEXT NOT NULL DEFAULT '',
                        source_file_relative_path TEXT NOT NULL,
                        source_line_number INTEGER NOT NULL DEFAULT 0,
                        source_text TEXT NOT NULL,
                        source_text_hash TEXT NOT NULL,
                        content_fingerprint TEXT NOT NULL,
                        classification_flags INTEGER NOT NULL DEFAULT 0,
                        xml_analyzer_version TEXT NOT NULL DEFAULT '',
                        xml_analysis_fingerprint TEXT NOT NULL DEFAULT '',
                        dll_analyzer_version TEXT NOT NULL DEFAULT '',
                        dll_analysis_fingerprint TEXT NOT NULL DEFAULT '',
                        ai_review_version TEXT NOT NULL DEFAULT '',
                        ai_review_fingerprint TEXT NOT NULL DEFAULT '',
                        updated_utc TEXT NOT NULL,
                        FOREIGN KEY(mod_identity) REFERENCES Mods(mod_identity) ON DELETE CASCADE);",
                    @"CREATE INDEX IF NOT EXISTS IX_Candidates_Mod ON Candidates(mod_identity);",
                    @"CREATE INDEX IF NOT EXISTS IX_Candidates_Class ON Candidates(classification_flags);",
                    @"CREATE INDEX IF NOT EXISTS IX_Candidates_File ON Candidates(mod_identity, source_file_relative_path);",
                    @"CREATE TABLE IF NOT EXISTS TranslationResults (
                        candidate_id TEXT NOT NULL,
                        target_language TEXT NOT NULL,
                        translation_state INTEGER NOT NULL DEFAULT 0,
                        translation_origin INTEGER NOT NULL DEFAULT 0,
                        translation_text TEXT NOT NULL DEFAULT '',
                        translation_hash TEXT NOT NULL DEFAULT '',
                        source_text_hash_at_translation TEXT NOT NULL DEFAULT '',
                        source_package_id TEXT NOT NULL DEFAULT '',
                        source_file_relative_path TEXT NOT NULL DEFAULT '',
                        source_entry_key TEXT NOT NULL DEFAULT '',
                        managed_output_file TEXT NOT NULL DEFAULT '',
                        managed_output_key TEXT NOT NULL DEFAULT '',
                        error_text TEXT NOT NULL DEFAULT '',
                        updated_utc TEXT NOT NULL,
                        PRIMARY KEY(candidate_id, target_language),
                        FOREIGN KEY(candidate_id) REFERENCES Candidates(candidate_id) ON DELETE CASCADE);",
                    @"CREATE INDEX IF NOT EXISTS IX_TranslationResults_State ON TranslationResults(target_language, translation_state);",
                    @"CREATE TABLE IF NOT EXISTS AnalysisRuns (
                        run_id TEXT PRIMARY KEY,
                        mod_identity TEXT NOT NULL,
                        source_domain INTEGER NOT NULL,
                        analyzer_version TEXT NOT NULL,
                        analysis_fingerprint TEXT NOT NULL,
                        forced INTEGER NOT NULL,
                        state INTEGER NOT NULL,
                        candidate_count INTEGER NOT NULL DEFAULT 0,
                        started_utc TEXT NOT NULL,
                        completed_utc TEXT,
                        error_text TEXT NOT NULL DEFAULT '');",
                    @"CREATE INDEX IF NOT EXISTS IX_AnalysisRuns_Lookup ON AnalysisRuns(mod_identity, source_domain, analysis_fingerprint, state);",
                    @"CREATE TABLE IF NOT EXISTS WorkflowRuns (
                        run_id TEXT PRIMARY KEY,
                        task_kind INTEGER NOT NULL,
                        state INTEGER NOT NULL,
                        input_json TEXT NOT NULL DEFAULT '',
                        result_json TEXT NOT NULL DEFAULT '',
                        started_utc TEXT NOT NULL,
                        completed_utc TEXT,
                        error_text TEXT NOT NULL DEFAULT '');",
                    @"CREATE TABLE IF NOT EXISTS DryRunReports (
                        run_id TEXT NOT NULL,
                        mod_identity TEXT NOT NULL,
                        report_json TEXT NOT NULL,
                        created_utc TEXT NOT NULL,
                        PRIMARY KEY(run_id, mod_identity));",
                    @"CREATE TABLE IF NOT EXISTS TranslationFileEntries (
                        relative_path TEXT NOT NULL,
                        entry_key TEXT NOT NULL,
                        target_language TEXT NOT NULL,
                        candidate_id TEXT,
                        observed_text TEXT NOT NULL,
                        observed_hash TEXT NOT NULL,
                        observed_utc TEXT NOT NULL,
                        PRIMARY KEY(relative_path, entry_key, target_language));",
                    @"CREATE INDEX IF NOT EXISTS IX_TranslationFileEntries_Candidate ON TranslationFileEntries(candidate_id);",
                    @"CREATE TABLE IF NOT EXISTS PendingFileOperations (
                        operation_id TEXT PRIMARY KEY,
                        candidate_id TEXT NOT NULL,
                        target_language TEXT NOT NULL,
                        operation_kind INTEGER NOT NULL,
                        translation_origin INTEGER NOT NULL DEFAULT 0,
                        relative_path TEXT NOT NULL,
                        entry_key TEXT NOT NULL,
                        desired_text TEXT NOT NULL,
                        desired_hash TEXT NOT NULL,
                        state INTEGER NOT NULL,
                        created_utc TEXT NOT NULL,
                        completed_utc TEXT,
                        error_text TEXT NOT NULL DEFAULT '');"
                },
                [2] = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS TranslationFileCache (
                        relative_path TEXT NOT NULL,
                        target_language TEXT NOT NULL,
                        content_hash TEXT NOT NULL,
                        entries_json TEXT NOT NULL,
                        observed_utc TEXT NOT NULL,
                        PRIMARY KEY(relative_path, target_language));"
                },
                [3] = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS WorkflowSettings (
                        setting_key TEXT PRIMARY KEY,
                        value_json TEXT NOT NULL,
                        updated_utc TEXT NOT NULL);"
                },
                [4] = new[]
                {
                    @"ALTER TABLE Candidates ADD COLUMN identity_schema_version TEXT NOT NULL DEFAULT 'atc-candidate-v1';",
                    @"ALTER TABLE Candidates ADD COLUMN entry_identity TEXT NOT NULL DEFAULT '';",
                    @"CREATE TABLE IF NOT EXISTS TranslationSyncState (
                        target_language TEXT PRIMARY KEY,
                        completed_generation INTEGER NOT NULL DEFAULT 0,
                        completed_utc TEXT NOT NULL,
                        result_json TEXT NOT NULL DEFAULT '');"
                },
                [5] = new[]
                {
                    @"ALTER TABLE Mods ADD COLUMN normalized_package_id TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Mods ADD COLUMN installation_source TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Mods ADD COLUMN installation_source_id TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Mods ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1;",
                    @"CREATE TABLE IF NOT EXISTS ModVersionSnapshots (
                        snapshot_id TEXT PRIMARY KEY,
                        mod_identity TEXT NOT NULL,
                        mod_version TEXT NOT NULL DEFAULT '',
                        game_version TEXT NOT NULL DEFAULT '',
                        load_folders_json TEXT NOT NULL DEFAULT '',
                        content_fingerprint TEXT NOT NULL,
                        created_utc TEXT NOT NULL,
                        is_current INTEGER NOT NULL DEFAULT 1,
                        FOREIGN KEY(mod_identity) REFERENCES Mods(mod_identity) ON DELETE CASCADE);",
                    @"CREATE INDEX IF NOT EXISTS IX_ModVersionSnapshots_Current
                        ON ModVersionSnapshots(mod_identity, is_current);",
                    @"ALTER TABLE ModFiles ADD COLUMN snapshot_id TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE ModFiles ADD COLUMN file_type TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE ModFiles ADD COLUMN last_write_utc TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE ModFiles ADD COLUMN is_present INTEGER NOT NULL DEFAULT 1;",
                    @"ALTER TABLE Candidates ADD COLUMN snapshot_id TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Candidates ADD COLUMN source_line_end INTEGER NOT NULL DEFAULT 0;",
                    @"ALTER TABLE Candidates ADD COLUMN estimated_tokens INTEGER NOT NULL DEFAULT 0;",
                    @"ALTER TABLE Candidates ADD COLUMN xml_reason_code TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Candidates ADD COLUMN dll_reason_code TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Candidates ADD COLUMN ai_review_reason TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Candidates ADD COLUMN manual_updated_utc TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE Candidates ADD COLUMN is_present INTEGER NOT NULL DEFAULT 1;",
                    @"ALTER TABLE TranslationResults ADD COLUMN validation_status TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN last_sync_status TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN last_sync_error TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN last_synced_utc TEXT NOT NULL DEFAULT '';"
                },
                [6] = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS ModInstallations (
                        mod_identity TEXT NOT NULL,
                        root_path TEXT NOT NULL,
                        installation_source TEXT NOT NULL DEFAULT '',
                        installation_source_id TEXT NOT NULL DEFAULT '',
                        is_active INTEGER NOT NULL DEFAULT 0,
                        last_observed_utc TEXT NOT NULL,
                        PRIMARY KEY(mod_identity, root_path),
                        FOREIGN KEY(mod_identity) REFERENCES Mods(mod_identity) ON DELETE CASCADE);",
                    @"CREATE INDEX IF NOT EXISTS IX_ModInstallations_Active
                        ON ModInstallations(mod_identity, is_active);"
                },
                [7] = new[]
                {
                    @"ALTER TABLE TranslationFileCache ADD COLUMN file_length INTEGER NOT NULL DEFAULT -1;",
                    @"ALTER TABLE TranslationFileCache ADD COLUMN last_write_utc TEXT NOT NULL DEFAULT '';"
                },
                [8] = new[]
                {
                    @"ALTER TABLE AnalysisRuns ADD COLUMN mod_version_fingerprint TEXT NOT NULL DEFAULT '';",
                    @"CREATE INDEX IF NOT EXISTS IX_AnalysisRuns_CurrentVersion
                        ON AnalysisRuns(mod_identity, mod_version_fingerprint, state);"
                },
                [9] = new[]
                {
                    @"ALTER TABLE PendingFileOperations ADD COLUMN source_text_hash TEXT NOT NULL DEFAULT '';"
                },
                [10] = new[]
                {
                    @"ALTER TABLE AnalysisRuns ADD COLUMN result_fingerprint TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE AnalysisRuns ADD COLUMN diagnostics_json TEXT NOT NULL DEFAULT '';"
                },
                [11] = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS WorkflowModReports (
                        run_id TEXT NOT NULL,
                        mod_identity TEXT NOT NULL,
                        task_kind INTEGER NOT NULL,
                        report_json TEXT NOT NULL,
                        created_utc TEXT NOT NULL,
                        PRIMARY KEY(run_id, mod_identity, task_kind));",
                    @"CREATE INDEX IF NOT EXISTS IX_WorkflowModReports_Latest
                        ON WorkflowModReports(mod_identity, task_kind, created_utc);"
                },
                [12] = new[]
                {
                    @"CREATE INDEX IF NOT EXISTS IX_Candidates_EditorPage
                        ON Candidates(mod_identity, is_present, source_domain,
                            source_file_relative_path, source_line_number, logical_locator, candidate_id);"
                },
                [13] = new[]
                {
                    @"CREATE INDEX IF NOT EXISTS IX_Candidates_AiCursor
                        ON Candidates(mod_identity, is_present,
                            source_file_relative_path, source_line_number, candidate_id);"
                },
                [14] = new[]
                {
                    @"CREATE INDEX IF NOT EXISTS IX_Candidates_SynchronizationCursor
                        ON Candidates(is_present, source_domain, candidate_id);"
                },
                [15] = new[]
                {
                    @"ALTER TABLE Candidates ADD COLUMN ai_review_prompt_version TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN ai_provider TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN ai_model TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN ai_prompt_version TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN ai_run_id TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE TranslationResults ADD COLUMN ai_batch_index INTEGER NOT NULL DEFAULT 0;",
                    @"ALTER TABLE PendingFileOperations ADD COLUMN ai_provider TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE PendingFileOperations ADD COLUMN ai_model TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE PendingFileOperations ADD COLUMN ai_prompt_version TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE PendingFileOperations ADD COLUMN ai_run_id TEXT NOT NULL DEFAULT '';",
                    @"ALTER TABLE PendingFileOperations ADD COLUMN ai_batch_index INTEGER NOT NULL DEFAULT 0;"
                },
                [16] = new[]
                {
                    @"CREATE INDEX IF NOT EXISTS IX_PendingFileOperations_Readiness
                        ON PendingFileOperations(candidate_id, target_language, state);"
                },
                [17] = new[]
                {
                    @"ALTER TABLE Mods ADD COLUMN is_installed INTEGER NOT NULL DEFAULT 1;",
                    @"CREATE TABLE IF NOT EXISTS NativeTranslationScans (
                        mod_identity TEXT NOT NULL,
                        target_language TEXT NOT NULL,
                        fingerprint TEXT NOT NULL,
                        state TEXT NOT NULL,
                        entry_count INTEGER NOT NULL DEFAULT 0,
                        error_text TEXT NOT NULL DEFAULT '',
                        scanned_utc TEXT NOT NULL,
                        PRIMARY KEY(mod_identity,target_language),
                        FOREIGN KEY(mod_identity) REFERENCES Mods(mod_identity) ON DELETE CASCADE);",
                    @"CREATE TABLE IF NOT EXISTS NativeTranslationEntries (
                        mod_identity TEXT NOT NULL,
                        target_language TEXT NOT NULL,
                        source_file TEXT NOT NULL,
                        bucket TEXT NOT NULL,
                        def_type TEXT NOT NULL,
                        entry_key TEXT NOT NULL,
                        translation_text TEXT NOT NULL,
                        PRIMARY KEY(mod_identity,target_language,source_file,bucket,def_type,entry_key),
                        FOREIGN KEY(mod_identity,target_language)
                            REFERENCES NativeTranslationScans(mod_identity,target_language) ON DELETE CASCADE);",
                    @"CREATE INDEX IF NOT EXISTS IX_NativeTranslationEntries_Lookup
                        ON NativeTranslationEntries(target_language,bucket,def_type,entry_key);"
                },
                [18] = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS ReferenceDictionaryEntries (
                        entry_id TEXT PRIMARY KEY,
                        target_language TEXT NOT NULL,
                        scope_mod_identity TEXT NULL,
                        source_form TEXT NOT NULL,
                        target_form TEXT NOT NULL,
                        part_of_speech TEXT NOT NULL DEFAULT '',
                        context_hint TEXT NOT NULL DEFAULT '',
                        example_text TEXT NOT NULL DEFAULT '',
                        source_reference TEXT NOT NULL DEFAULT '',
                        is_builtin INTEGER NOT NULL DEFAULT 0,
                        enabled INTEGER NOT NULL DEFAULT 1,
                        updated_utc TEXT NOT NULL,
                        FOREIGN KEY(scope_mod_identity) REFERENCES Mods(mod_identity));",
                    @"CREATE INDEX IF NOT EXISTS IX_ReferenceDictionaryEntries_LanguageScope
                        ON ReferenceDictionaryEntries(target_language,scope_mod_identity,enabled);"
                },
                [20] = new[]
                {
                    @"ALTER TABLE PendingFileOperations ADD COLUMN reuse_provenance_json TEXT NOT NULL DEFAULT '';",
                    @"CREATE INDEX IF NOT EXISTS IX_Candidates_ReuseHash ON Candidates(source_text_hash);"
                },
                [19] = new[]
                {
                    @"ALTER TABLE AnalysisRuns ADD COLUMN target_language TEXT NOT NULL DEFAULT '';",
                    @"CREATE INDEX IF NOT EXISTS IX_AnalysisRuns_Language
                        ON AnalysisRuns(mod_identity,source_domain,target_language,state);",
                    @"CREATE INDEX IF NOT EXISTS IX_AnalysisRuns_LatestCompleted
                        ON AnalysisRuns(mod_identity,source_domain,state);"
                }
            };

        public static void EnsureCurrent(DbConnection connection)
        {
            int version = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA user_version;"));
            if (version > CurrentVersion)
                throw new InvalidOperationException("Database schema is newer than this mod version: " + version);

            for (int target = version + 1; target <= CurrentVersion; target++)
            {
                using (DbTransaction transaction = connection.BeginTransaction())
                {
                    foreach (string sql in Migrations[target]) ExecuteNonQuery(connection, transaction, sql);
                    ExecuteNonQuery(connection, transaction, "PRAGMA user_version = " + target + ";");
                    transaction.Commit();
                }
            }
        }

        private static object ExecuteScalar(DbConnection connection, string sql)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }

        private static void ExecuteNonQuery(DbConnection connection, DbTransaction transaction, string sql)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
    }
}
