using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DBVC.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;

namespace DBVC.Core
{
    public class SmoManager
    {
        private readonly ConfigManager _configManager;

        public SmoManager(ConfigManager? configManager = null)
        {
            _configManager = configManager ?? new ConfigManager();
        }

        public bool ScriptObjects(string serverName, string databaseName, List<string>? objectNames = null)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return false;
            }

            string localGitPath;
            try
            {
                localGitPath = _configManager.GetMapping(serverName, databaseName);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(localGitPath))
            {
                return false;
            }

            try
            {
                var connStr = new SqlConnectionStringBuilder
                {
                    DataSource = serverName,
                    InitialCatalog = databaseName,
                    IntegratedSecurity = true,
                    TrustServerCertificate = true
                }.ToString();

                using var sqlConn = new SqlConnection(connStr);
                var conn = new ServerConnection(sqlConn);
                var server = new Server(conn);
                var db = server.Databases[databaseName];

                if (db == null)
                {
                    return false;
                }

                var scripter = new Scripter(server)
                {
                    Options = new ScriptingOptions
                    {
                        ScriptDrops = false,
                        IncludeIfNotExists = false,
                        ToFileOnly = true,
                        AppendToFile = false
                    }
                };

                // For MVP, iterate Tables as a proof of concept.
                foreach (Table tb in db.Tables)
                {
                    if (tb.IsSystemObject) continue;

                    if (objectNames != null && objectNames.Count > 0 &&
                        !objectNames.Contains(tb.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var dir = Path.Combine(localGitPath, tb.Schema, "Tables");
                    Directory.CreateDirectory(dir);
                    scripter.Options.FileName = Path.Combine(dir, $"{tb.Name}.sql");
                    scripter.Script(new[] { tb.Urn });
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}


