using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
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
