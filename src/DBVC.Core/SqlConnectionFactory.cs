using System;
using DBVC.Core.Models;
using Microsoft.Data.SqlClient;

namespace DBVC.Core
{
    /// <summary>
    /// 연결 문자열을 만드는 유일한 지점.
    ///
    /// 전에는 <see cref="StateTracker"/>와 <see cref="SmoManager"/>가 각자
    /// <c>IntegratedSecurity = true</c>를 하드코딩한 같은 블록을 들고 있었다. 한 곳에 모아 두어야
    /// 인증 방식이 두 경로에서 갈라지지 않는다.
    /// </summary>
    public class SqlConnectionFactory
    {
        private readonly ISqlCredentialStore _credentialStore;

        public SqlConnectionFactory(ISqlCredentialStore? credentialStore = null)
        {
            _credentialStore = credentialStore ?? new SqlCredentialStore();
        }

        /// <summary>
        /// 저장된 인증 정보로 연결 문자열을 만든다.
        /// 인증 정보가 없으면 Windows 통합 인증으로 간주한다 — SQL 인증이 도입되기 전에
        /// 매핑해 둔 데이터베이스가 그대로 동작해야 하기 때문이다.
        /// </summary>
        /// <exception cref="SqlCredentialException">
        /// SQL 인증으로 설정되어 있으나 암호를 확보할 수 없는 경우.
        /// </exception>
        public string Build(string serverName, string databaseName)
        {
            var credential = _credentialStore.TryGet(serverName, databaseName);

            if (credential == null || credential.AuthMode != SqlAuthMode.Sql)
            {
                return BuildWindows(serverName, databaseName);
            }

            var password = _credentialStore.ResolvePassword(credential);
            if (string.IsNullOrEmpty(credential.UserName) || password == null)
            {
                throw new SqlCredentialException(
                    $"'{serverName}.{databaseName}'은(는) SQL 인증으로 설정되어 있으나 저장된 암호를 사용할 수 없습니다. " +
                    "Connect에서 사용자명과 암호를 다시 입력하세요. " +
                    "(암호는 저장한 Windows 계정에서만 복호화됩니다 — 다른 계정으로 로그온했다면 다시 입력해야 합니다.)");
            }

            return BuildSql(serverName, databaseName, credential.UserName!, password!);
        }

        /// <summary>Windows 통합 인증 연결 문자열.</summary>
        public static string BuildWindows(string serverName, string databaseName)
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = serverName,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ToString();
        }

        /// <summary>SQL Server 인증 연결 문자열.</summary>
        public static string BuildSql(string serverName, string databaseName, string userName, string password)
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = serverName,
                InitialCatalog = databaseName,
                IntegratedSecurity = false,
                UserID = userName,
                Password = password,
                // 연결 후 ConnectionString 속성에서 암호가 다시 읽히지 않게 한다.
                PersistSecurityInfo = false,
                TrustServerCertificate = true
            }.ToString();
        }
    }
}
