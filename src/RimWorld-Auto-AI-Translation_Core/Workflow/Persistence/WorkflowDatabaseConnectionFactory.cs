using System;
using System.Data.Common;
using System.IO;
using System.Reflection;

namespace AutoTranslator_Core.Workflow.Persistence
{
    internal sealed class WorkflowDatabaseConnectionFactory
    {
        private readonly string _coreModRoot;
        private readonly string _generatedPackRoot;
        private readonly object _sync = new object();
        private DbProviderFactory _providerFactory;

        public WorkflowDatabaseConnectionFactory(string coreModRoot, string generatedPackRoot)
        {
            _coreModRoot = coreModRoot ?? throw new ArgumentNullException(nameof(coreModRoot));
            _generatedPackRoot = generatedPackRoot ?? throw new ArgumentNullException(nameof(generatedPackRoot));
            DatabasePath = Path.Combine(_generatedPackRoot, "Database", "AutoTranslationCore.sqlite3");
        }

        public string DatabasePath { get; }

        public DbConnection OpenConnection()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath));
            DbConnection connection = GetProviderFactory().CreateConnection();
            if (connection == null) throw new InvalidOperationException("SQLite provider did not create a connection.");
            connection.ConnectionString = "Data Source=" + DatabasePath + ";Version=3;Pooling=True;Journal Mode=WAL;Synchronous=Normal;Foreign Keys=True;Busy Timeout=5000;";
            connection.Open();
            using (DbCommand command = connection.CreateCommand())
            {
                // Keep correctness-critical settings explicit instead of relying
                // solely on provider-specific connection-string parsing.
                command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                command.ExecuteNonQuery();
            }
            return connection;
        }

        public void DeleteDatabaseFilesAfterConfirmedCorruption()
        {
            DeleteIfExists(DatabasePath);
            DeleteIfExists(DatabasePath + "-wal");
            DeleteIfExists(DatabasePath + "-shm");
        }

        private DbProviderFactory GetProviderFactory()
        {
            lock (_sync)
            {
                if (_providerFactory != null) return _providerFactory;
                Assembly assembly = SQLiteNativeBootstrapper.PrepareAndLoadProvider(_coreModRoot, _generatedPackRoot);
                Type factoryType = assembly.GetType("System.Data.SQLite.SQLiteFactory", true);
                FieldInfo instanceField = factoryType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                _providerFactory = instanceField?.GetValue(null) as DbProviderFactory;
                if (_providerFactory == null) throw new InvalidOperationException("System.Data.SQLite.SQLiteFactory.Instance is unavailable.");
                return _providerFactory;
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
