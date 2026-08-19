using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 통합 테스트용 임시 데이터베이스. 접속할 수 없으면 <see cref="TryCreate"/>가 null을 준다 —
    /// CI(windows-latest)와 비Windows 개발 환경 어느 쪽도 SQL Server를 보장하지 않으므로
    /// 없는 환경을 강요하지 않고 건너뛴다.
    /// </summary>
    public sealed class SqlServerTestDatabase : IDisposable
    {
        public const string ServerName = "localhost";

        /// <summary>이 접두사로 시작하는 DB만 정리 대상으로 본다.</summary>
        public const string Prefix = "DBVC_ITest_";

        public string Name { get; }

        private SqlServerTestDatabase(string name) { Name = name; }

        public static SqlServerTestDatabase? TryCreate(out string? skipReason)
        {
            var name = Prefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                DropStaleDatabases();
                ExecuteOnMaster("CREATE DATABASE [" + name + "]");
                skipReason = null;
                return new SqlServerTestDatabase(name);
            }
            catch (Exception ex)
            {
                skipReason = "SQL Server '" + ServerName + "'에 접속할 수 없어 통합 테스트를 건너뜁니다: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 이전 실행이 남긴 데이터베이스를 지운다. 생성된 지 한 시간이 지난 것만 건드린다 —
        /// 시각 조건이 없으면 같은 서버에서 동시에 도는 다른 실행의 것을 지운다.
        /// </summary>
        private static void DropStaleDatabases()
        {
            var stale = new List<string>();
            using (var conn = OpenMaster())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name FROM sys.databases " +
                    "WHERE name LIKE @prefix + '%' AND create_date < DATEADD(hour, -1, GETDATE())";
                cmd.Parameters.AddWithValue("@prefix", Prefix);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) stale.Add(reader.GetString(0));
            }

            foreach (var name in stale)
            {
                try
                {
                    ExecuteOnMaster("ALTER DATABASE [" + name + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                    ExecuteOnMaster("DROP DATABASE [" + name + "]");
                }
                catch (Exception ex)
                {
                    TestContextWrite("남은 테스트 데이터베이스 '" + name + "'를 지우지 못했습니다: " + ex.Message);
                }
            }
        }

        public void Execute(string sql)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>여러 문장을 한 연결에서 순서대로 실행한다. EXECUTE AS / REVERT처럼 세션을 공유해야 하는 경우에 쓴다.</summary>
        public void ExecuteInOneSession(params string[] statements)
        {
            using var conn = Open();
            foreach (var sql in statements)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public object? QueryScalar(string sql)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }

        public SqlConnection Open()
        {
            var conn = new SqlConnection(SqlConnectionFactory.BuildWindows(ServerName, Name));
            conn.Open();
            return conn;
        }

        private static SqlConnection OpenMaster()
        {
            var connString = new SqlConnectionStringBuilder(
                SqlConnectionFactory.BuildWindows(ServerName, "master")) { ConnectTimeout = 2 }.ToString();
            var conn = new SqlConnection(connString);
            conn.Open();
            return conn;
        }

        private static void ExecuteOnMaster(string sql)
        {
            using var conn = OpenMaster();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static void TestContextWrite(string message) => TestContext.Out.WriteLine(message);

        public void Dispose()
        {
            try
            {
                ExecuteOnMaster("ALTER DATABASE [" + Name + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                ExecuteOnMaster("DROP DATABASE [" + Name + "]");
            }
            catch (Exception ex)
            {
                TestContextWrite("테스트 데이터베이스를 지우지 못했습니다: " + ex.Message);
            }
        }
    }
}
