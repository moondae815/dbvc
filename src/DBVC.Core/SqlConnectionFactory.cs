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
            _credentialStore = credentialStore ?? new SessionCredentialStore();
        }

        /// <summary>
        /// 보관된 인증 정보로 연결 문자열을 만든다.
        /// 인증 정보가 없으면 Windows 통합 인증으로 간주한다 — 정상 흐름에서는 Connect가 항상
        /// 인증 정보를 넣으므로 닿지 않는 갈래이고, 남겨 두는 것은 방어다.
        /// </summary>
        /// <exception cref="SqlCredentialException">
        /// SQL 인증으로 설정되어 있으나 계정명이나 암호가 없는 경우.
        /// </exception>
        public string Build(string serverName, string databaseName)
        {
            var credential = _credentialStore.TryGet(serverName, databaseName);

            if (credential == null || credential.AuthMode != SqlAuthMode.Sql)
            {
                return BuildWindows(serverName, databaseName);
            }

            if (string.IsNullOrEmpty(credential.UserName) || string.IsNullOrEmpty(credential.Password))
            {
                throw new SqlCredentialException(
                    $"'{serverName}.{databaseName}'은(는) SQL 인증으로 설정되어 있으나 암호를 사용할 수 없습니다. " +
                    "SSMS 개체 탐색기에서 이 데이터베이스에 접속한 뒤 DBVC 창에서 Connect를 누르세요. " +
                    "(인증 정보는 SSMS를 닫으면 사라지므로 재시작 후에는 다시 눌러야 합니다.)");
            }

            return BuildSql(serverName, databaseName, credential.UserName!, credential.Password!);
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
