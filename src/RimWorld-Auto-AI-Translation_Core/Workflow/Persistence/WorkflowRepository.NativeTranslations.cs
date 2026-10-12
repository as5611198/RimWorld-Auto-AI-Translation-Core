using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using AutoTranslator_Core.Workflow.Analysis;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal sealed partial class WorkflowRepository
    {
        private HashSet<string> _loadedNativeSources = new HashSet<string>(StringComparer.Ordinal);
        private List<string> _nativeLoadOrder = new List<string>();

        internal void SetLoadedNativeTranslationSources(IList<string> identities)
        {
            _loadedNativeSources = new HashSet<string>(identities, StringComparer.Ordinal);
            _nativeLoadOrder = identities.ToList();
        }

        internal HashSet<string> GetIncompleteNativeTranslationPackages(string targetLanguage)
        {
            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT provider.mod_identity,provider.package_id
                    FROM NativeTranslationScans scan
                    JOIN Mods provider ON provider.mod_identity=scan.mod_identity
                    WHERE scan.target_language=@target AND scan.state IN ('Failed','Partial');";
                Add(command, "@target", targetLanguage);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                        if (_loadedNativeSources.Contains(reader.GetString(0)))
                            packages.Add(reader.GetString(1));
            }
            return packages;
        }

        internal ModCatalogSummary SynchronizeModCatalog(IList<ModSnapshotRecord> mods, CancellationToken token,
            Action<int, int, string> progress = null)
        {
            var current = mods.GroupBy(mod => mod.ModIdentity, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(mod => mod.IsActive).First()).ToList();
            var previous = new Dictionary<string, Tuple<bool, string, string>>(StringComparer.Ordinal);
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                using (DbCommand read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT mod_identity,is_active,root_path,version_label FROM Mods WHERE is_installed=1;";
                    using (DbDataReader reader = read.ExecuteReader())
                        while (reader.Read()) previous[reader.GetString(0)] = Tuple.Create(
                            Convert.ToInt32(reader[1]) != 0, reader.GetString(2), reader.GetString(3));
                }
                var summary = new ModCatalogSummary
                {
                    Installed = current.Count, Enabled = current.Count(mod => mod.IsActive),
                    IsInitial = previous.Count == 0,
                    Added = current.Count(mod => !previous.ContainsKey(mod.ModIdentity)),
                    Removed = previous.Keys.Count(id => !current.Any(mod => mod.ModIdentity == id))
                };
                summary.NewlyEnabled = current.Count(mod => mod.IsActive &&
                    (!previous.TryGetValue(mod.ModIdentity, out var old) || !old.Item1));
                summary.Disabled = previous.Count(pair => pair.Value.Item1 &&
                    !current.Any(mod => mod.ModIdentity == pair.Key && mod.IsActive));
                summary.Unchanged = summary.Added == 0 && summary.Removed == 0 &&
                    summary.NewlyEnabled == 0 && summary.Disabled == 0 && current.All(mod =>
                        previous.TryGetValue(mod.ModIdentity, out var old) &&
                        WorkflowPath.Comparer.Equals(old.Item2, mod.RootPath) && old.Item3 == mod.VersionLabel);
                using (DbCommand clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "UPDATE Mods SET is_installed=0,is_active=0; UPDATE ModInstallations SET is_active=0;";
                    clear.ExecuteNonQuery();
                }
                int completed = 0;
                foreach (ModSnapshotRecord mod in current)
                {
                    token.ThrowIfCancellationRequested();
                    using (DbCommand write = connection.CreateCommand())
                    {
                        write.Transaction = transaction;
                        write.CommandText = @"INSERT INTO Mods
                            (mod_identity,package_id,normalized_package_id,display_name,root_path,version_label,
                             version_fingerprint,last_observed_utc,is_active,is_installed)
                            VALUES (@id,@package,@normalized,@name,@root,@version,'',@now,@active,1)
                            ON CONFLICT(mod_identity) DO UPDATE SET package_id=excluded.package_id,
                            normalized_package_id=excluded.normalized_package_id,display_name=excluded.display_name,
                            root_path=excluded.root_path,version_label=excluded.version_label,
                            last_observed_utc=excluded.last_observed_utc,is_active=excluded.is_active,is_installed=1;";
                        Add(write,"@id",mod.ModIdentity); Add(write,"@package",mod.PackageId);
                        Add(write,"@normalized",mod.NormalizedPackageId); Add(write,"@name",mod.DisplayName);
                        Add(write,"@root",mod.RootPath); Add(write,"@version",mod.VersionLabel);
                        Add(write,"@now",ToDbTime(DateTime.UtcNow)); Add(write,"@active",mod.IsActive ? 1 : 0);
                        write.ExecuteNonQuery();
                    }
                    completed++;
                    progress?.Invoke(completed,current.Count,mod.DisplayName);
                }
                foreach (ModSnapshotRecord mod in mods)
                {
                    token.ThrowIfCancellationRequested();
                    using (DbCommand write = connection.CreateCommand())
                    {
                        write.Transaction = transaction;
                        write.CommandText = @"INSERT INTO ModInstallations
                            (mod_identity,root_path,is_active,last_observed_utc) VALUES (@id,@root,@active,@now)
                            ON CONFLICT(mod_identity,root_path) DO UPDATE SET is_active=excluded.is_active,
                            last_observed_utc=excluded.last_observed_utc;";
                        Add(write,"@id",mod.ModIdentity); Add(write,"@root",mod.RootPath);
                        Add(write,"@active",mod.IsActive ? 1 : 0); Add(write,"@now",ToDbTime(DateTime.UtcNow));
                        write.ExecuteNonQuery();
                    }
                }
                token.ThrowIfCancellationRequested();
                transaction.Commit();
                return summary;
            }
        }

        internal bool HasNativeTranslationScan(string mod, string language, string fingerprint)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT COUNT(*) FROM NativeTranslationScans
                    WHERE mod_identity=@mod AND target_language=@language AND fingerprint=@fingerprint AND state='Completed';";
                Add(command,"@mod",mod); Add(command,"@language",language); Add(command,"@fingerprint",fingerprint);
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
        }

        internal void ReplaceNativeTranslationScan(string mod, string language, string fingerprint,
            IList<NativeTranslationEntry> entries, string error = "")
        {
            entries = entries.GroupBy(entry => entry.SourceFile + "\n" + entry.Bucket + "\n" +
                entry.DefType + "\n" + entry.EntryKey, StringComparer.Ordinal).Select(group => group.Last()).ToList();
            using (DbConnection connection = _connections.OpenConnection())
            using (DbTransaction transaction = connection.BeginTransaction())
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"INSERT INTO NativeTranslationScans
                        (mod_identity,target_language,fingerprint,state,entry_count,error_text,scanned_utc)
                        VALUES (@mod,@language,@fingerprint,@state,@count,@error,@now)
                        ON CONFLICT(mod_identity,target_language) DO UPDATE SET fingerprint=excluded.fingerprint,
                        state=excluded.state,entry_count=excluded.entry_count,error_text=excluded.error_text,scanned_utc=excluded.scanned_utc;";
                    Add(command,"@mod",mod); Add(command,"@language",language); Add(command,"@fingerprint",fingerprint);
                    Add(command,"@count",entries.Count); Add(command,"@now",ToDbTime(DateTime.UtcNow));
                    Add(command,"@state",string.IsNullOrEmpty(error) ? "Completed" : "Partial");
                    Add(command,"@error",error ?? string.Empty);
                    command.ExecuteNonQuery();
                    command.Parameters.Clear();
                    command.CommandText = "DELETE FROM NativeTranslationEntries WHERE mod_identity=@mod AND target_language=@language;";
                    Add(command,"@mod",mod); Add(command,"@language",language); command.ExecuteNonQuery();
                }
                foreach (NativeTranslationEntry entry in entries)
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"INSERT OR REPLACE INTO NativeTranslationEntries
                            (mod_identity,target_language,source_file,bucket,def_type,entry_key,translation_text)
                            VALUES (@mod,@language,@file,@bucket,@type,@key,@text);";
                        Add(command,"@mod",mod); Add(command,"@language",language); Add(command,"@file",entry.SourceFile);
                        Add(command,"@bucket",entry.Bucket); Add(command,"@type",entry.DefType);
                        Add(command,"@key",entry.EntryKey); Add(command,"@text",entry.Text); command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        internal void FailNativeTranslationScan(string mod, string language, string error)
        {
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO NativeTranslationScans
                    (mod_identity,target_language,fingerprint,state,error_text,scanned_utc)
                    VALUES (@mod,@language,'','Failed',@error,@now)
                    ON CONFLICT(mod_identity,target_language) DO UPDATE SET state='Failed',
                    error_text=excluded.error_text,scanned_utc=excluded.scanned_utc;";
                Add(command,"@mod",mod); Add(command,"@language",language); Add(command,"@error",error);
                Add(command,"@now",ToDbTime(DateTime.UtcNow)); command.ExecuteNonQuery();
            }
        }

        internal List<NativeTranslationEntry> GetNativeTranslations(string language)
        {
            var entries = new List<NativeTranslationEntry>();
            using (DbConnection connection = _connections.OpenConnection())
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT e.mod_identity,m.package_id,e.source_file,e.bucket,e.def_type,e.entry_key,e.translation_text
                    FROM NativeTranslationEntries e JOIN NativeTranslationScans s
                    ON s.mod_identity=e.mod_identity AND s.target_language=e.target_language
                    JOIN Mods m ON m.mod_identity=e.mod_identity
                    WHERE e.target_language=@language AND s.state IN ('Completed','Partial') ORDER BY e.mod_identity,e.source_file;";
                Add(command,"@language",language);
                using (DbDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                        if (_loadedNativeSources.Contains(reader.GetString(0))) entries.Add(new NativeTranslationEntry
                        {
                            ModIdentity=reader.GetString(0),PackageId=reader.GetString(1),SourceFile=reader.GetString(2),
                            Bucket=reader.GetString(3),DefType=reader.GetString(4),EntryKey=reader.GetString(5),Text=reader.GetString(6)
                        });
            }
            return entries.OrderBy(entry => _nativeLoadOrder.IndexOf(entry.ModIdentity)).ToList();
        }
    }
}
