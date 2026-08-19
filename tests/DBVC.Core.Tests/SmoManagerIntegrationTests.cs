using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 실제 SQL Server에 대고 SMO 추출 경로를 실행한다.
    ///
    /// 이 경로는 오랫동안 어떤 테스트로도 실행되지 않았다. 테스트 프로젝트가 SMO 181(asm
    /// 18.100)을, DBVC.Core가 171(asm 17.100)을 참조해 런타임에 TypeLoadException이 났고,
    /// SmoManager가 그 예외를 삼켜 null을 돌려주었으며, 유일한 테스트가 "성공 또는 실패
    /// 둘 다 허용"으로 쓰여 있어 조용히 통과했다. 이 픽스처가 그 구멍을 막는다.
    ///
    /// 서버에 접속할 수 없으면 건너뛴다 — CI는 windows-latest이고 주 개발 환경은 macOS라
    /// 어느 쪽도 SQL Server를 보장하지 않는다. 실패로 만들면 없는 환경을 강요하게 된다.
    /// </summary>
    [TestFixture]
    public class SmoManagerIntegrationTests
    {
        private const string ServerName = "localhost";

        /// <summary>접속할 수 없으면 null. 그러면 모든 테스트가 건너뛴다.</summary>
        private static string? _database;
        private static string? _skipReason;

        [OneTimeSetUp]
        public void CreateTestDatabase()
        {
            var name = "DBVC_ITest_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                var connString = new SqlConnectionStringBuilder(SqlConnectionFactory.BuildWindows(ServerName, "master"))
                {
                    ConnectTimeout = 1
                }.ToString();
                using var conn = new SqlConnection(connString);
                conn.Open();

                Execute(conn, "CREATE DATABASE [" + name + "]");
                Execute(conn, "USE [" + name + "]");

                // EnumerateTargets의 여러 갈래를 한 번에 지나가도록 타입을 섞는다.
                Execute(conn, "CREATE TABLE dbo.Users (Id int IDENTITY(1,1) PRIMARY KEY, Name nvarchar(100) NOT NULL)");
                Execute(conn, "CREATE VIEW dbo.vUsers AS SELECT Id, Name FROM dbo.Users");
                Execute(conn, "CREATE PROCEDURE dbo.usp_GetUser @Id int AS SELECT Id, Name FROM dbo.Users WHERE Id = @Id");
                Execute(conn, "CREATE FUNCTION dbo.fn_Double(@n int) RETURNS int AS BEGIN RETURN @n * 2 END");
                Execute(conn, "CREATE TRIGGER dbo.trg_Users_Ins ON dbo.Users AFTER INSERT AS BEGIN SET NOCOUNT ON END");

                _database = name;
            }
            catch (Exception ex)
            {
                _skipReason = "SQL Server '" + ServerName + "'에 접속할 수 없어 SMO 통합 테스트를 건너뜁니다: " + ex.Message;
            }
        }

        [OneTimeTearDown]
        public void DropTestDatabase()
        {
            if (_database == null) return;

            try
            {
                var connString = new SqlConnectionStringBuilder(SqlConnectionFactory.BuildWindows(ServerName, "master"))
                {
                    ConnectTimeout = 1
                }.ToString();
                using var conn = new SqlConnection(connString);
                conn.Open();
                Execute(conn, "ALTER DATABASE [" + _database + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                Execute(conn, "DROP DATABASE [" + _database + "]");
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine("테스트 데이터베이스를 지우지 못했습니다: " + ex.Message);
            }
        }

        [SetUp]
        public void SkipWhenNoServer()
        {
            if (_database == null) Assert.Ignore(_skipReason ?? "SQL Server에 접속할 수 없습니다.");
        }

        [Test]
        public void ScriptObjectsDetailed_ExtractsEveryObjectType_FromARealDatabase()
        {
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);

            Assert.That(result, Is.Not.Null, "추출이 시작조차 못 했습니다. SmoManager가 예외를 삼켰습니다.");
            Assert.That(result!.FailedObjects, Is.Empty);
            Assert.That(repo.RelativePaths(), Is.EquivalentTo(new[]
            {
                "dbo/Tables/Users.sql",
                "dbo/Views/vUsers.sql",
                "dbo/StoredProcedures/usp_GetUser.sql",
                "dbo/Functions/fn_Double.sql",
                "dbo/Triggers/trg_Users_Ins.sql"
            }));
        }

        [Test]
        public void ScriptObjectsDetailed_WritesTheActualCreateStatement()
        {
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);
            Assert.That(result, Is.Not.Null, "Initial script failed");

            var sql = File.ReadAllText(Path.Combine(repo.Path, "dbo", "StoredProcedures", "usp_GetUser.sql"));
            Assert.That(sql, Does.Contain("CREATE").And.Contain("usp_GetUser"));
        }

        [Test]
        public void ScriptObjectsDetailed_ExtractsOnlyTheNamedObjects_WhenFiltered()
        {
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, new List<string> { "dbo.Users" });

            Assert.That(result, Is.Not.Null, "Initial script failed");
            Assert.That(result.SucceededCount, Is.EqualTo(1));
            Assert.That(repo.RelativePaths(), Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
        }

        [Test]
        public void ScriptObjectsDetailed_SucceedsWithoutCreatingFiles_WhenNothingMatchesTheFilter()
        {
            // 지운 SmoManagerTests의 테스트가 여기서 걸렸다. 추출할 것이 없어도 성공이고,
            // 성공했다고 폴더가 생기지는 않는다. 사용자 객체가 없는 데이터베이스가 같은 경우다.
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, new List<string> { "dbo.NoSuchObject" });

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.SucceededCount, Is.EqualTo(0));
            Assert.That(result.FailedObjects, Is.Empty);
            Assert.That(repo.RelativePaths(), Is.Empty);
        }

        [Test]
        public void ScriptObjectsDetailed_DoesNotRewriteFilesWhoseContentDidNotChange()
        {
            // ScriptAll의 단위 테스트는 가짜 스크립터로 검증한다. 실제 SMO 출력이 두 번의 호출에서
            // 바이트까지 같은지는 여기서만 알 수 있고, 같지 않으면 git status 최적화가 무너진다.
            using var repo = new TempRepo(_database!);

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);
            Assert.That(result, Is.Not.Null, "Initial script failed");

            var path = Path.Combine(repo.Path, "dbo", "Tables", "Users.sql");
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);

            var result2 = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null);
            Assert.That(result2, Is.Not.Null, "Second script failed");

            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp),
                "SMO가 같은 내용을 냈는데도 파일을 다시 썼습니다. git이 전 파일을 다시 해시하게 됩니다.");
        }

        [Test]
        public void ScriptObjectsDetailed_ReportsProgressForEveryObject()
        {
            using var repo = new TempRepo(_database!);
            var reported = new List<ExtractionProgress>();

            var result = repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null,
                new ImmediateProgress(reported.Add), CancellationToken.None);

            Assert.That(result, Is.Not.Null, "Initial script failed");
            Assert.That(reported, Is.Not.Empty);
            Assert.That(reported[reported.Count - 1].Completed, Is.EqualTo(reported[reported.Count - 1].Total));
            Assert.That(reported.Select(p => p.Completed), Is.Ordered);
        }

        [Test]
        public void ScriptObjectsDetailed_StopsAndPropagatesCancellation()
        {
            using var repo = new TempRepo(_database!);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                repo.Smo.ScriptObjectsDetailed(ServerName, _database!, null, null, cts.Token));

            Assert.That(repo.RelativePaths(), Is.Empty, "취소된 추출이 파일을 남겼습니다.");
        }

        private static void Execute(SqlConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>매핑까지 갖춘 임시 저장소 폴더. 사용자의 실제 mappings.json을 건드리지 않는다.</summary>
        private sealed class TempRepo : IDisposable
        {
            public string Path { get; }
            public SmoManager Smo { get; }

            public TempRepo(string database)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbvc_it_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);

                var config = new ConfigManager(System.IO.Path.Combine(Path, "mappings.json"));
                config.AddMapping(ServerName, database, Path);
                Smo = new SmoManager(config);
            }

            public string[] RelativePaths()
                => Directory.GetFiles(Path, "*.sql", SearchOption.AllDirectories)
                    .Select(f => f.Substring(Path.Length + 1).Replace('\\', '/'))
                    .OrderBy(p => p)
                    .ToArray();

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch { }
            }
        }

        private sealed class ImmediateProgress : IProgress<ExtractionProgress>
        {
            private readonly Action<ExtractionProgress> _onReport;
            public ImmediateProgress(Action<ExtractionProgress> onReport) { _onReport = onReport; }
            public void Report(ExtractionProgress value) => _onReport(value);
        }
    }
}
