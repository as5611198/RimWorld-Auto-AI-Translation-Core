using System;
using System.Collections.Generic;
using System.Data.Common;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal sealed partial class WorkflowRepository
    {
        private void InitializeReferenceDictionary(DbConnection connection)
        {
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                foreach (ReferenceDictionaryEntry entry in BuiltInReferenceDictionary.Entries())
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT OR IGNORE INTO ReferenceDictionaryEntries
                            (entry_id,target_language,scope_mod_identity,source_form,target_form,part_of_speech,
                             context_hint,example_text,source_reference,is_builtin,enabled,updated_utc)
                            VALUES (@id,@language,@mod,@source,@target,@pos,@context,@example,@reference,1,1,@now);";
                        BindDictionaryEntry(command, entry);
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        internal List<ReferenceDictionaryEntry> GetReferenceDictionaryEntries(string language, bool includeDisabled = false)
        {
            var entries = new List<ReferenceDictionaryEntry>();
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT entry_id,target_language,scope_mod_identity,source_form,target_form,
                    part_of_speech,context_hint,example_text,source_reference,is_builtin,enabled
                    FROM ReferenceDictionaryEntries WHERE target_language=@language
                    AND (@all=1 OR enabled=1) ORDER BY source_form,context_hint,entry_id;";
                Add(command,"@language",language); Add(command,"@all",includeDisabled ? 1 : 0);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) entries.Add(new ReferenceDictionaryEntry
                    {
                        EntryId=reader.GetString(0), TargetLanguage=reader.GetString(1),
                        ScopeModIdentity=reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        SourceForm=reader.GetString(3),TargetForm=reader.GetString(4),PartOfSpeech=reader.GetString(5),
                        ContextHint=reader.GetString(6),ExampleText=reader.GetString(7),SourceReference=reader.GetString(8),
                        IsBuiltIn=Convert.ToInt32(reader[9])!=0,Enabled=Convert.ToInt32(reader[10])!=0
                    });
            }
            return entries;
        }

        internal void SaveReferenceDictionaryEntry(ReferenceDictionaryEntry entry)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO ReferenceDictionaryEntries
                    (entry_id,target_language,scope_mod_identity,source_form,target_form,part_of_speech,
                     context_hint,example_text,source_reference,is_builtin,enabled,updated_utc)
                    VALUES (@id,@language,@mod,@source,@target,@pos,@context,@example,@reference,0,@enabled,@now)
                    ON CONFLICT(entry_id) DO UPDATE SET scope_mod_identity=excluded.scope_mod_identity,
                    source_form=excluded.source_form,target_form=excluded.target_form,part_of_speech=excluded.part_of_speech,
                    context_hint=excluded.context_hint,example_text=excluded.example_text,source_reference=excluded.source_reference,
                    is_builtin=0,enabled=excluded.enabled,updated_utc=excluded.updated_utc
                    WHERE ReferenceDictionaryEntries.target_language=excluded.target_language;";
                BindDictionaryEntry(command,entry); Add(command,"@enabled",entry.Enabled ? 1 : 0);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("词条所属语种不匹配，未保存修改。");
            }
        }

        internal List<WorkflowModMetadataSnapshot> GetReferenceDictionaryScopes()
        {
            var mods = new List<WorkflowModMetadataSnapshot>();
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT mod_identity,display_name,package_id FROM Mods ORDER BY display_name;";
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read()) mods.Add(new WorkflowModMetadataSnapshot
                        { ModIdentity=reader.GetString(0),DisplayName=reader.GetString(1),PackageId=reader.GetString(2) });
            }
            return mods;
        }

        private static void BindDictionaryEntry(DbCommand command, ReferenceDictionaryEntry entry)
        {
            Add(command,"@id",entry.EntryId); Add(command,"@language",entry.TargetLanguage);
            Add(command,"@mod",string.IsNullOrEmpty(entry.ScopeModIdentity) ? null : entry.ScopeModIdentity);
            Add(command,"@source",entry.SourceForm); Add(command,"@target",entry.TargetForm);
            Add(command,"@pos",entry.PartOfSpeech ?? string.Empty); Add(command,"@context",entry.ContextHint ?? string.Empty);
            Add(command,"@example",entry.ExampleText ?? string.Empty); Add(command,"@reference",entry.SourceReference ?? string.Empty);
            Add(command,"@now",ToDbTime(DateTime.UtcNow));
        }
    }
}
