using System;
using System.Collections.Generic;
using System.Data.Common;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal sealed partial class WorkflowRepository
    {
        public List<UploadTranslationSourceRecord> GetUploadTranslationSources(string packageId, string targetLanguage)
        {
            List<UploadTranslationSourceRecord> rows = new List<UploadTranslationSourceRecord>();
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT c.candidate_id, c.entry_kind, c.context_json,
                    COALESCE(NULLIF(t.managed_output_key,''),c.output_entry_key,'') AS entry_key,
                    t.translation_text, t.translation_hash, t.translation_origin, t.managed_output_file, t.ai_provider
                    FROM Candidates c INNER JOIN Mods m ON m.mod_identity=c.mod_identity
                    INNER JOIN TranslationResults t ON t.candidate_id=c.candidate_id
                    WHERE m.package_id=@package COLLATE NOCASE AND t.target_language=@language
                    AND c.is_present=1 AND c.mod_version_fingerprint=m.version_fingerprint
                    AND c.source_domain=@xml AND t.translation_state=@translated
                    AND t.source_text_hash_at_translation=c.source_text_hash
                    AND length(trim(t.translation_text))>0;";
                Add(command, "@package", packageId ?? string.Empty);
                Add(command, "@language", targetLanguage ?? string.Empty);
                Add(command, "@translated", (int)CandidateTranslationState.Translated);
                Add(command, "@xml", (int)CandidateSourceDomain.Xml);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new UploadTranslationSourceRecord
                        {
                            CandidateId = Convert.ToString(reader["candidate_id"]),
                            EntryKind = Convert.ToString(reader["entry_kind"]),
                            ContextJson = Convert.ToString(reader["context_json"]),
                            EntryKey = Convert.ToString(reader["entry_key"]),
                            TranslationText = Convert.ToString(reader["translation_text"]),
                            TranslationHash = Convert.ToString(reader["translation_hash"]),
                            OutputFile = Convert.ToString(reader["managed_output_file"]),
                            AiProvider = Convert.ToString(reader["ai_provider"]),
                            Origin = (TranslationOrigin)Convert.ToInt32(reader["translation_origin"])
                        });
            }
            return rows;
        }
    }

    internal sealed class UploadTranslationSourceRecord
    {
        public string CandidateId;
        public string EntryKind;
        public string ContextJson;
        public string EntryKey;
        public string TranslationText;
        public string TranslationHash;
        public string OutputFile;
        public string AiProvider;
        public TranslationOrigin Origin;
    }
}
