using System;
using System.Collections.Generic;
using DBVC.Core.Models;

namespace DBVC.Core
{
    public class ConfigManager
    {
        private readonly Dictionary<string, MappingConfig> _mappings = new Dictionary<string, MappingConfig>(StringComparer.OrdinalIgnoreCase);

        public void AddMapping(MappingConfig mapping)
        {
            if (mapping == null)
            {
                throw new ArgumentNullException(nameof(mapping));
            }
            string key = GetKey(mapping.ServerName, mapping.DatabaseName);
            _mappings[key] = mapping;
        }

        public string GetMapping(string serverName, string databaseName)
        {
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
