using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

[assembly: InternalsVisibleTo("DBVC.Core.Tests")]

namespace DBVC.Core
{
    public class StateTracker
    {
        private readonly ConfigManager _configManager;
        private readonly ConcurrentDictionary<string, string> _stateCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public StateTracker() : this(new ConfigManager())
        {
        }

        public StateTracker(ConfigManager configManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        public bool IsInitialized(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return false;
            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND type in (N'U')";
                var result = cmd.ExecuteScalar();
                return result != null && Convert.ToInt32(result) > 0;
            }
            catch
            {
                return false;
            }
        }

        public void InitializeDatabase(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Invalid connection string", nameof(connectionString));

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InstallTrigger.sql");
            if (stream == null) throw new FileNotFoundException("InstallTrigger.sql not found in embedded resources.");
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd();

            var batches = script.Split(new[] { "\r\nGO", "\nGO", "GO\r\n", "GO\n" }, StringSplitOptions.RemoveEmptyEntries);

            using var conn = new SqlConnection(connectionString);
            conn.Open();
            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = batch;
                cmd.ExecuteNonQuery();
            }
        }

        public void RefreshState(string serverName, string databaseName)
        {
            var mapping = _configManager.GetMapping(serverName, databaseName);
            if (string.IsNullOrWhiteSpace(mapping)) return;

            try
            {
                var connStr = new SqlConnectionStringBuilder { DataSource = serverName, InitialCatalog = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }.ToString();
                using var conn = new SqlConnection(connStr);
                conn.Open();
                
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ObjectName, EventType FROM DBVC_ChangeLog ORDER BY EventDate DESC";
                
                using var reader = cmd.ExecuteReader();
                var rows = new List<(string ObjectName, string EventType)>();
                while (reader.Read())
                {
                    var objName = reader.GetString(0);
                    var evType = reader.GetString(1);
                    rows.Add((objName, evType));
                }
                ProcessChangeLogRows(serverName, databaseName, rows);
            }
            catch (SqlException ex)
            {
                // Graceful fail if DB/table doesn't exist yet
                Debug.WriteLine($"StateTracker.RefreshState SqlException: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Graceful fail on connection or driver errors with diagnostics
                Debug.WriteLine($"StateTracker.RefreshState Exception: {ex.Message}");
            }
        }

        internal void ProcessChangeLogRows(string serverName, string databaseName, IEnumerable<(string ObjectName, string EventType)> rows)
        {
            var seenObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (objName, evType) in rows)
            {
                var key = $"{serverName}.{databaseName}.{objName}";
                if (seenObjects.Add(key))
                {
                    _stateCache[key] = evType;
                }
            }
        }
        
        public string GetObjectState(string serverName, string databaseName, string objectName)
        {
            if (_stateCache.TryGetValue($"{serverName}.{databaseName}.{objectName}", out var state))
                return state;
            return "Clean";
        }

        public List<string> GetPendingChanges(string connectionString)
        {
            return new List<string>();
        }
    }
}
