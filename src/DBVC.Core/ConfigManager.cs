using System;
using System.Collections.Concurrent;
using DBVC.Core.Models;

namespace DBVC.Core
{
    public class ConfigManager
    {
        private readonly ConcurrentDictionary<string, MappingConfig> _mappings = new ConcurrentDictionary<string, MappingConfig>(StringComparer.OrdinalIgnoreCase);

        public void AddMapping(MappingConfig mapping)
        {
            if (mapping == null)
            {
                throw new ArgumentNullException(nameof(mapping));
            }
            if (string.IsNullOrWhiteSpace(mapping.ServerName))
            {
                throw new ArgumentException("ServerName cannot be null or whitespace.", nameof(mapping));
            }
            if (string.IsNullOrWhiteSpace(mapping.DatabaseName))
            {
                throw new ArgumentException("DatabaseName cannot be null or whitespace.", nameof(mapping));
            }
            string key = GetKey(mapping.ServerName, mapping.DatabaseName);
            _mappings[key] = mapping;
        }

        public void AddMapping(string serverName, string databaseName, string gitPath)
        {
            AddMapping(new MappingConfig { ServerName = serverName, DatabaseName = databaseName, GitPath = gitPath });
        }

        public string GetMapping(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                throw new ArgumentException("ServerName cannot be null or whitespace.", nameof(serverName));
            }
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("DatabaseName cannot be null or whitespace.", nameof(databaseName));
            }

            string key = GetKey(serverName, databaseName);
            if (_mappings.TryGetValue(key, out var mapping) && !string.IsNullOrEmpty(mapping.GitPath))
            {
                return mapping.GitPath;
            }

            // Minimal default implementation path
            return $@"C:\Git\{serverName}\{databaseName}";
        }

        private static string GetKey(string serverName, string databaseName)
        {
            return $"{serverName}::{databaseName}";
        }
    }
}
