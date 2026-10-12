using System;
using System.Data.Common;
using Newtonsoft.Json;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal sealed class ReusableTranslation
    {
        public string Text { get; set; }
        public TranslationOrigin Origin { get; set; }
        public string SourcePackage { get; set; }
        public string SourceFile { get; set; }
        public string SourceKey { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string PromptVersion { get; set; }
        public string RunId { get; set; }
        public int BatchIndex { get; set; }
    }

    internal sealed partial class WorkflowRepository
    {
        public ReusableTranslation FindReusableTranslation(CandidateRecord candidate, string targetLanguage)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT r.* FROM Candidates c
                    JOIN TranslationResults r ON r.candidate_id=c.candidate_id
                    WHERE c.source_text_hash=@hash AND c.source_text=@text COLLATE BINARY
                      AND c.candidate_id<>@id AND r.target_language=@target
                      AND r.source_text_hash_at_translation=c.source_text_hash
                      AND r.translation_origin>0 AND r.translation_text<>''
                      AND r.validation_status<>'Invalid'
                      AND (r.translation_state=2 OR r.translation_origin>1)
                      AND r.last_sync_status NOT IN ('ReadError','SourceChanged','SourceMissing')
                      AND NOT EXISTS (SELECT 1 FROM PendingFileOperations p
                          WHERE p.candidate_id=c.candidate_id AND p.target_language=@target AND p.state IN (0,2))
                    ORDER BY r.translation_origin DESC,r.updated_utc DESC,c.candidate_id LIMIT 1;";
                Add(command, "@hash", candidate.SourceTextHash);
                Add(command, "@text", candidate.SourceText);
                Add(command, "@id", candidate.CandidateId);
                Add(command, "@target", targetLanguage);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new ReusableTranslation
                    {
                        Text = Convert.ToString(reader["translation_text"]),
                        Origin = (TranslationOrigin)Convert.ToInt32(reader["translation_origin"]),
                        SourcePackage = Convert.ToString(reader["source_package_id"]),
                        SourceFile = Convert.ToString(reader["source_file_relative_path"]),
                        SourceKey = Convert.ToString(reader["source_entry_key"]),
                        Provider = Convert.ToString(reader["ai_provider"]),
                        Model = Convert.ToString(reader["ai_model"]),
                        PromptVersion = Convert.ToString(reader["ai_prompt_version"]),
                        RunId = Convert.ToString(reader["ai_run_id"]),
                        BatchIndex = Convert.ToInt32(reader["ai_batch_index"])
                    };
                }
            }
        }

        private static void RestoreReuseProvenance(DbConnection connection, DbTransaction transaction,
            PendingTranslationCompletion completion)
        {
            string json;
            using (DbCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT reuse_provenance_json FROM PendingFileOperations WHERE operation_id=@operation;";
                Add(read, "@operation", completion.OperationId.ToString("N"));
                json = Convert.ToString(read.ExecuteScalar());
            }
            if (string.IsNullOrEmpty(json)) return;
            ReusableTranslation source = JsonConvert.DeserializeObject<ReusableTranslation>(json);
            if (source == null) throw new InvalidOperationException("Missing reused translation provenance.");
            using (DbCommand update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"UPDATE TranslationResults SET translation_origin=@origin,
                    source_package_id=@package,source_file_relative_path=@file,source_entry_key=@key,
                    ai_provider=@provider,ai_model=@model,ai_prompt_version=@prompt,ai_run_id=@run,ai_batch_index=@batch
                    WHERE candidate_id=@id AND target_language=@target;";
                Add(update, "@origin", (int)source.Origin);
                Add(update, "@package", source.SourcePackage ?? "");
                Add(update, "@file", source.SourceFile ?? "");
                Add(update, "@key", source.SourceKey ?? "");
                Add(update, "@provider", source.Provider ?? "");
                Add(update, "@model", source.Model ?? "");
                Add(update, "@prompt", source.PromptVersion ?? "");
                Add(update, "@run", source.RunId ?? "");
                Add(update, "@batch", source.BatchIndex);
                Add(update, "@id", completion.CandidateId);
                Add(update, "@target", completion.TargetLanguage);
                update.ExecuteNonQuery();
            }
        }
    }
}
