using System;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 설치 스크립트가 만드는 트리거를 실제 SQL Server에서 검증한다.
    ///
    /// 이 파일이 없던 동안 트리거 SQL에는 어떤 테스트도 닿지 않았고, 그 사이 두 결함이 살아남았다 —
    /// 권한 없는 사용자의 DDL이 통째로 실패하는 것과, XML 메서드가 SET 옵션에 의존하는 것.
    /// 접속할 수 없으면 건너뛴다.
    /// </summary>
    [TestFixture]
    public class DdlTriggerIntegrationTests
    {
        private static SqlServerTestDatabase? _db;
        private static string? _skipReason;

        [OneTimeSetUp]
        public void CreateDatabase()
        {
            _db = SqlServerTestDatabase.TryCreate(out _skipReason);
            if (_db == null) return;

            // 실제 설치 경로를 그대로 쓴다. 여기서 raw SQL을 돌리면 SqlClient가 보내는
            // SET 옵션까지 포함한 "진짜 설치"를 검증하지 못한다.
            new StateTracker(NewConfig()).InitializeDatabase(SqlServerTestDatabase.ServerName, _db.Name);
        }

        [OneTimeTearDown]
        public void DropDatabase() => _db?.Dispose();

        [SetUp]
        public void SkipWhenNoServer()
        {
            if (_db == null) Assert.Ignore(_skipReason ?? "SQL Server에 접속할 수 없습니다.");
        }

        private static ConfigManager NewConfig()
            => new ConfigManager(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dbvc_cfg_" + Guid.NewGuid().ToString("N"), "mappings.json"));

        [Test]
        public void Trigger_LetsALowPrivilegedUserRunDdl_AndStillLogsIt()
        {
            // 트리거가 사용자 권한으로 INSERT하면 ChangeLog에 쓸 수 없는 사용자의 DDL이
            // 오류 3616으로 롤백된다 — DBVC를 쓰지 않는 팀원까지 막는다.
            _db!.ExecuteInOneSession(
                "CREATE USER dbvc_low_t1 WITHOUT LOGIN",
                "GRANT CREATE TABLE TO dbvc_low_t1",
                "GRANT ALTER ON SCHEMA::dbo TO dbvc_low_t1");

            Assert.DoesNotThrow(() => _db.ExecuteInOneSession(
                "EXECUTE AS USER = 'dbvc_low_t1'",
                "CREATE TABLE dbo.LowPrivTable (Id int)",
                "REVERT"));

            var logged = _db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'LowPrivTable'");
            Assert.That(Convert.ToInt32(logged), Is.EqualTo(1), "DDL은 성공했는데 로그가 남지 않았습니다");
        }

        [Test]
        public void Trigger_DoesNotLogEvents_ForObjectTypesDbvcCannotScript()
        {
            // 사용자·권한 이벤트는 파일이 만들어질 수 없어 목록에 대응하는 .sql이 없는 항목으로 남는다.
            // 그 항목만 체크해 커밋하면 "커밋할 변경사항이 없습니다"만 나오고 영원히 사라지지 않는다.
            _db!.ExecuteInOneSession(
                "CREATE USER dbvc_ghost_t2 WITHOUT LOGIN",
                "GRANT SELECT TO dbvc_ghost_t2");

            var ghosts = _db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE ObjectType IN (N'USER', N'DATABASE')");

            Assert.That(Convert.ToInt32(ghosts), Is.Zero);
        }
    }
}
