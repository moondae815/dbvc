using System;
using System.Collections.Concurrent;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// (서버, 데이터베이스)별 SQL 접속 인증 정보를 이 프로세스가 사는 동안만 보관한다.
    ///
    /// 값의 출처는 SSMS 개체 탐색기뿐이므로 디스크에 남길 이유가 없다 — SSMS를 다시 열면
    /// 개체 탐색기에 다시 접속하게 되고, 그 순간 최신 값이 다시 들어온다.
    ///
    /// <b>이 클래스는 파일을 모른다.</b> 옛 credentials.json 정리는
    /// <see cref="LegacyCredentialFile"/>가 따로 맡는다. 여기에 파일 접근을 들이면
    /// "디스크에 아무것도 쓰지 않는다"는 계약을 단위 테스트로 증명할 수 없게 된다.
    /// </summary>
    public class SessionCredentialStore : ISqlCredentialStore
    {
        private readonly ConcurrentDictionary<string, SqlCredential> _credentials =
            new ConcurrentDictionary<string, SqlCredential>(StringComparer.OrdinalIgnoreCase);

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
        /// 이 대상의 인증 정보를 통째로 덮어쓴다.
        ///
        /// 옛 <c>Save</c>의 "<c>plainPassword == null</c>이면 저장된 암호를 그대로 둔다"는 병합
        /// 규칙을 물려받지 않는다. 그 규칙은 디스크에 이전 값이 있다는 전제 위에서만 뜻이 있었고,
        /// 지금은 개체 탐색기가 준 네 값이 언제나 최신이다.
        /// </summary>
        public void Set(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                throw new ArgumentException("ServerName cannot be null or whitespace.", nameof(serverName));
            }
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("DatabaseName cannot be null or whitespace.", nameof(databaseName));
            }

            _credentials[GetKey(serverName, databaseName)] = new SqlCredential
            {
                ServerName = serverName,
                DatabaseName = databaseName,
                AuthMode = authMode,
                // Windows 인증에는 둘 다 의미가 없다. 들고 있으면 인증 방식만 바뀐 뒤에도
                // 남아서 언젠가 잘못된 대상으로 나간다.
                UserName = authMode == SqlAuthMode.Sql ? userName : null,
                Password = authMode == SqlAuthMode.Sql ? password : null
            };
        }

        /// <summary>키 규약은 옛 파일 저장소와 같다. 대소문자를 무시한다.</summary>
        private static string GetKey(string serverName, string databaseName)
        {
            return $"{serverName}::{databaseName}";
        }
    }
}
