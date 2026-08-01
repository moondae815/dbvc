using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// SQL Server 데이터베이스와 로컬 Git 저장소의 매핑을 관리한다.
    /// 매핑은 설계 문서(4.2)에 따라 <c>%APPDATA%\DBVC\mappings.json</c>에 영속화된다.
    /// </summary>
    public class ConfigManager : IConfigManager
    {
        private readonly ConcurrentDictionary<string, MappingConfig> _mappings = new ConcurrentDictionary<string, MappingConfig>(StringComparer.OrdinalIgnoreCase);
        private readonly string _configFilePath;
        private readonly object _fileLock = new object();

        public static string DefaultConfigFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBVC",
            "mappings.json");

        public ConfigManager() : this(DefaultConfigFilePath)
        {
        }

        public ConfigManager(string configFilePath)
        {
            if (string.IsNullOrWhiteSpace(configFilePath))
            {
                throw new ArgumentException("Config file path cannot be null or whitespace.", nameof(configFilePath));
            }
            _configFilePath = configFilePath;
            Load();
        }

        public string ConfigFilePath => _configFilePath;

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
            Save();
        }

        public void AddMapping(string serverName, string databaseName, string gitPath)
        {
            AddMapping(new MappingConfig { ServerName = serverName, DatabaseName = databaseName, GitPath = gitPath });
        }

        public bool RemoveMapping(string serverName, string databaseName)
        {
            ValidateServerAndDatabase(serverName, databaseName);

            bool removed = _mappings.TryRemove(GetKey(serverName, databaseName), out _);
            if (removed)
            {
                Save();
            }
            return removed;
        }

        /// <summary>
        /// 매핑을 조회한다. 등록된 매핑이 없으면 <c>null</c>을 반환한다.
        /// </summary>
        public MappingConfig? TryGetMapping(string serverName, string databaseName)
        {
            ValidateServerAndDatabase(serverName, databaseName);

            if (_mappings.TryGetValue(GetKey(serverName, databaseName), out var mapping)
                && !string.IsNullOrWhiteSpace(mapping.GitPath))
            {
                return mapping;
            }
            return null;
        }

        /// <summary>
        /// 매핑된 로컬 Git 경로를 반환한다. 매핑이 없으면 <c>null</c>을 반환한다.
        /// 호출자는 반드시 null 여부를 확인해야 한다.
        /// </summary>
        public string? GetMapping(string serverName, string databaseName)
        {
            return TryGetMapping(serverName, databaseName)?.GitPath;
        }

        public IReadOnlyList<MappingConfig> GetAllMappings()
        {
            return _mappings.Values.ToList();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    return;
                }

                var json = File.ReadAllText(_configFilePath);
                var mappings = MappingConfigSerializer.Deserialize(json);
                if (mappings == null)
                {
                    return;
                }

                foreach (var mapping in mappings)
                {
                    if (mapping == null
                        || string.IsNullOrWhiteSpace(mapping.ServerName)
                        || string.IsNullOrWhiteSpace(mapping.DatabaseName))
                    {
                        continue;
                    }
                    _mappings[GetKey(mapping.ServerName, mapping.DatabaseName)] = mapping;
                }
            }
            catch (Exception ex)
            {
                // 손상된 설정 파일 때문에 플러그인 전체가 죽지 않도록 무시하고 빈 상태로 시작한다.
                Debug.WriteLine($"ConfigManager.Load failed for '{_configFilePath}': {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                lock (_fileLock)
                {
                    var directory = Path.GetDirectoryName(_configFilePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.WriteAllText(_configFilePath, MappingConfigSerializer.Serialize(_mappings.Values.ToList()));
                }
            }
            catch (Exception ex)
            {
                // 저장 실패가 메모리 내 매핑 사용을 막아서는 안 된다.
                Debug.WriteLine($"ConfigManager.Save failed for '{_configFilePath}': {ex.Message}");
            }
        }

        private static void ValidateServerAndDatabase(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                throw new ArgumentException("ServerName cannot be null or whitespace.", nameof(serverName));
            }
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("DatabaseName cannot be null or whitespace.", nameof(databaseName));
            }
        }

        private static string GetKey(string serverName, string databaseName)
        {
            return $"{serverName}::{databaseName}";
        }
    }
}
