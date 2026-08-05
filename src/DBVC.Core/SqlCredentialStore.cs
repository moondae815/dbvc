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
    /// (서버, 데이터베이스)별 SQL 접속 인증 정보를 <c>%APPDATA%\DBVC\credentials.json</c>에 보관한다.
    ///
    /// 매핑(<see cref="ConfigManager"/>)과 파일을 나눈 이유가 있다. Connect는 저장소 매핑보다
    /// 먼저 일어나고(매핑되지 않은 DB에도 접속해 초기화 여부를 판정한다), 매핑을 지워도
    /// 접속 정보는 남는 편이 자연스럽다. 두 수명이 다르므로 한 파일에 묶지 않는다.
    ///
    /// 암호는 <see cref="IPasswordProtector"/>를 거친 형태로만 기록한다. 평문은 파일에 닿지 않는다.
    /// </summary>
    public class SqlCredentialStore : ISqlCredentialStore
    {
        private readonly ConcurrentDictionary<string, SqlCredential> _credentials =
            new ConcurrentDictionary<string, SqlCredential>(StringComparer.OrdinalIgnoreCase);

        private readonly string _filePath;
        private readonly IPasswordProtector _protector;
        private readonly SessionPasswordCache _sessionPasswords;
        private readonly object _fileLock = new object();

        public static string DefaultFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DBVC",
            "credentials.json");

        public SqlCredentialStore() : this(DefaultFilePath, null)
        {
        }

        public SqlCredentialStore(
            string filePath,
            IPasswordProtector? protector = null,
            SessionPasswordCache? sessionPasswords = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Credential file path cannot be null or whitespace.", nameof(filePath));
            }
            _filePath = filePath;
            _protector = protector ?? new DpapiPasswordProtector();
            _sessionPasswords = sessionPasswords ?? new SessionPasswordCache();
            Load();
        }

        public string FilePath => _filePath;

        public bool CanPersistPasswords => _protector.IsSupported;

        public SqlCredential? TryGet(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            {
                return null;
            }
            return _credentials.TryGetValue(GetKey(serverName, databaseName), out var credential)
                ? credential
                : null;
        }

        /// <summary>
        /// 인증 정보를 저장한다.
        /// </summary>
        /// <param name="plainPassword">
        /// 평문 암호. <c>null</c>이면 이미 저장된 암호를 그대로 둔다 — 사용자가 암호 칸을 비운 채
        /// 다시 Connect했을 때 저장해 둔 암호를 지우지 않기 위해서다.
        /// 암호를 실제로 지우려면 빈 문자열을 준다.
        /// </param>
        /// <returns>
        /// 요청대로 저장됐으면 true. SQL 인증인데 이 플랫폼에서 암호를 보호할 수 없어
        /// 암호를 뺀 채 저장한 경우 false.
        /// </returns>
        public bool Save(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? plainPassword)
        {
            ValidateServerAndDatabase(serverName, databaseName);

            string key = GetKey(serverName, databaseName);
            var existing = TryGet(serverName, databaseName);

            var credential = new SqlCredential
            {
                ServerName = serverName,
                DatabaseName = databaseName,
                AuthMode = authMode,
                UserName = authMode == SqlAuthMode.Sql ? userName : null
            };

            bool fullySaved = true;

            if (authMode != SqlAuthMode.Sql)
            {
                // Windows 인증으로 되돌렸다면 남은 암호를 들고 있을 이유가 없다.
                credential.ProtectedPassword = null;
            }
            else if (plainPassword == null)
            {
                credential.ProtectedPassword = existing?.ProtectedPassword;
            }
            else if (plainPassword.Length == 0)
            {
                credential.ProtectedPassword = null;
            }
            else
            {
                credential.ProtectedPassword = _protector.Protect(plainPassword, key);
                fullySaved = credential.ProtectedPassword != null;
            }

            // 세션 암호(SSMS에서 가져온 값)를 언제 버리는지가 우선순위 규칙이다.
            //   - Windows 인증으로 되돌렸다 → 암호 자체가 필요 없다
            //   - 평문이 들어왔다(빈 문자열 포함) → 사용자가 직접 입력했으므로 그 값이 이긴다
            // plainPassword == null은 "저장된 것을 그대로 둔다"는 뜻이고 SSMS 경로가 쓰는 형태이므로
            // 여기서 지우면 안 된다.
            if (authMode != SqlAuthMode.Sql || plainPassword != null)
            {
                _sessionPasswords.Remove(serverName, databaseName);
            }

            _credentials[key] = credential;
            SaveToDisk();
            return fullySaved;
        }

        public bool Remove(string serverName, string databaseName)
        {
            ValidateServerAndDatabase(serverName, databaseName);

            bool removed = _credentials.TryRemove(GetKey(serverName, databaseName), out _);
            if (removed)
            {
                SaveToDisk();
            }
            return removed;
        }

        public void SetSessionPassword(string serverName, string databaseName, string? plainPassword)
        {
            ValidateServerAndDatabase(serverName, databaseName);
            _sessionPasswords.Set(serverName, databaseName, plainPassword);
        }

        /// <summary>
        /// 보호된 암호를 평문으로 되돌린다. 저장된 암호가 없거나 이 계정에서 풀 수 없으면 <c>null</c>.
        ///
        /// 세션 암호를 먼저 본다. SSMS에서 방금 가져온 연결이 예전에 저장해 둔 암호보다 최신이고,
        /// 애초에 디스크에 없는 값이므로 이 경로가 아니면 쓰일 곳이 없다.
        /// </summary>
        public string? ResolvePassword(SqlCredential? credential)
        {
            if (credential == null)
            {
                return null;
            }

            var sessionPassword = _sessionPasswords.TryGet(credential.ServerName, credential.DatabaseName);
            if (sessionPassword != null)
            {
                return sessionPassword;
            }

            if (string.IsNullOrEmpty(credential.ProtectedPassword))
            {
                return null;
            }

            return _protector.Unprotect(
                credential.ProtectedPassword,
                GetKey(credential.ServerName, credential.DatabaseName));
        }

        public IReadOnlyList<SqlCredential> GetAll()
        {
            return _credentials.Values.ToList();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return;
                }

                var credentials = SqlCredentialSerializer.Deserialize(File.ReadAllText(_filePath));
                if (credentials == null)
                {
                    return;
                }

                foreach (var credential in credentials)
                {
                    if (credential == null
                        || string.IsNullOrWhiteSpace(credential.ServerName)
                        || string.IsNullOrWhiteSpace(credential.DatabaseName))
                    {
                        continue;
                    }
                    _credentials[GetKey(credential.ServerName, credential.DatabaseName)] = credential;
                }
            }
            catch (Exception ex)
            {
                // 손상된 파일 하나로 플러그인 전체가 죽지 않도록 빈 상태로 시작한다.
                // 사용자는 Connect에서 다시 입력하면 된다.
                Debug.WriteLine($"SqlCredentialStore.Load failed for '{_filePath}': {ex.Message}");
            }
        }

        private void SaveToDisk()
        {
            try
            {
                lock (_fileLock)
                {
                    var directory = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.WriteAllText(_filePath, SqlCredentialSerializer.Serialize(_credentials.Values.ToList()));
                }
            }
            catch (Exception ex)
            {
                // 저장 실패가 이번 세션의 접속까지 막아서는 안 된다.
                Debug.WriteLine($"SqlCredentialStore.SaveToDisk failed for '{_filePath}': {ex.Message}");
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
