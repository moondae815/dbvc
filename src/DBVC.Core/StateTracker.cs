using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace DBVC.Core
{
    public class StateTracker
    {
        private readonly ConfigManager _configManager;
        private readonly ConcurrentDictionary<string, string> _stateCache = new ConcurrentDictionary<string, string>();

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
            if (mapping == null) return;

            try
            {
                var connStr = new SqlConnectionStringBuilder { DataSource = serverName, InitialCatalog = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }.ToString();
                using var conn = new SqlConnection(connStr);
                conn.Open();
                
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ObjectName, EventType FROM DBVC_ChangeLog ORDER BY EventDate DESC";
                
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var objName = reader.GetString(0);
                    var evType = reader.GetString(1);
                    _stateCache[$"{serverName}.{databaseName}.{objName}"] = evType;
                }
            }
            catch (SqlException)
            {
                // Graceful fail if DB/table doesn't exist yet
            }
            catch (Exception)
            {
                // Graceful fail on connection or driver errors
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
