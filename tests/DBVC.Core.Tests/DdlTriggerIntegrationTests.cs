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
            // ObjectType 값은 이벤트마다 다르다(예: CREATE_USER는 'USER'가 아니라 'SQL USER') -
            // 특정 값으로 필터링하면 그 값 하나만 검증하고 나머지(또는 NULL)가 새는 것은 숨긴다.
            // 그래서 전체 행수의 증가분(0이어야 한다)으로 검증한다. 픽스처가 DB를 공유하므로
            // 절대값이 아니라 문장 실행 전후의 차이를 본다.
            var before = Convert.ToInt32(_db!.QueryScalar("SELECT COUNT(*) FROM dbo.DBVC_ChangeLog"));

            _db.ExecuteInOneSession(
                "CREATE USER dbvc_ghost_t2 WITHOUT LOGIN",
                "GRANT SELECT TO dbvc_ghost_t2",
                "CREATE ROLE dbvc_ghost_role_t2",
                "CREATE SCHEMA dbvc_ghost_schema_t2");

            var after = Convert.ToInt32(_db.QueryScalar("SELECT COUNT(*) FROM dbo.DBVC_ChangeLog"));

            Assert.That(after - before, Is.Zero);
        }

        [Test]
        public void Trigger_RecordsTheParentTable_ForIndexEvents()
        {
            // 부모를 남기지 않으면 새로고침이 인덱스 이름만 추출 대상으로 잡고 테이블을 건드리지 않는다.
            // 0.2.4부터 인덱스는 테이블 스크립트에 담기므로, 저장소가 데이터베이스와 조용히 어긋난다.
            _db!.Execute("CREATE TABLE dbo.IndexedTable (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL)");
            _db.Execute("CREATE NONCLUSTERED INDEX IX_IndexedTable_Name ON dbo.IndexedTable (Name)");

            var target = _db.QueryScalar(
                "SELECT TargetObjectName FROM dbo.DBVC_ChangeLog " +
                "WHERE ObjectName = N'IX_IndexedTable_Name' AND EventType = N'CREATE_INDEX'");

            Assert.That(target, Is.EqualTo("IndexedTable"));
        }

        [Test]
        public void MarkProcessed_ClosesTheIndexRow_WhenTheParentTableIsCommitted()
        {
            _db!.Execute("CREATE TABLE dbo.MarkedTable (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL)");
            _db.Execute("CREATE NONCLUSTERED INDEX IX_MarkedTable_Name ON dbo.MarkedTable (Name)");

            var tracker = new StateTracker(NewConfig());
            var maxId = Convert.ToInt64(_db.QueryScalar(
                "SELECT MAX(Id) FROM dbo.DBVC_ChangeLog WHERE ObjectName IN (N'MarkedTable', N'IX_MarkedTable_Name')"));

            // ReadPendingRows의 넓어진 쿼리·리더 매핑과 인덱스→부모 정규화가 실 DB를 상대로
            // 끝까지 왕복하는 것을 검증하는 유일한 자리다 - MarkProcessed 이전에 확인해야
            // 아래에서 행을 닫아도 이 시점의 상태를 정확히 본 것이라 말할 수 있다.
            var changedNames = tracker.GetChangedObjectNames(SqlServerTestDatabase.ServerName, _db.Name);
            Assert.That(changedNames, Has.Some.EqualTo("dbo.MarkedTable"));
            Assert.That(changedNames, Has.None.Matches<string>(n => n.Contains("IX_MarkedTable_Name")),
                "정규화된 목록에 인덱스 이름이 그대로 남아 있으면 안 된다");

            tracker.MarkProcessed(SqlServerTestDatabase.ServerName, _db.Name, new[]
            {
                new DBVC.Core.Models.ChangeRecord
                {
                    Schema = "dbo",
                    ObjectName = "MarkedTable",
                    ObjectType = "TABLE",
                    State = "Modified",
                    QualifiedName = "dbo.MarkedTable",
                    RelativePath = "dbo/Tables/MarkedTable.sql",
                    LastLogId = maxId
                }
            });

            var open = _db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog " +
                "WHERE IsProcessed = 0 AND ObjectName IN (N'MarkedTable', N'IX_MarkedTable_Name')");

            Assert.That(Convert.ToInt32(open), Is.Zero, "테이블을 커밋하면 딸린 인덱스 행도 함께 닫혀야 한다");
        }

        [Test]
        public void GetInstalledVersion_ReturnsTheRequiredVersion_AfterInstall()
        {
            var version = new StateTracker(NewConfig())
                .GetInstalledVersion(SqlServerTestDatabase.ServerName, _db!.Name);

            Assert.That(version, Is.EqualTo(StateTracker.RequiredSchemaVersion));
        }

        [Test]
        public void InstallScript_IsIdempotent_WhenRunTwice()
        {
            // 재설치는 업데이트 경로이기도 하다. 두 번째 실행이 실패하면 구버전 사용자가 올라갈 길이 없다.
            var tracker = new StateTracker(NewConfig());
            Assert.DoesNotThrow(() => tracker.InitializeDatabase(SqlServerTestDatabase.ServerName, _db!.Name));
            Assert.DoesNotThrow(() => tracker.InitializeDatabase(SqlServerTestDatabase.ServerName, _db!.Name));
        }

        [Test]
        public void InstallScript_ClosesRowsThatCanNeverBeCommitted_WhenUpgradingFromV1()
        {
            // v1이 남긴 두 종류를 닫는다: 파일이 생길 수 없는 타입(사용자·권한)과,
            // 부모를 모르는 인덱스 행. 그대로 두면 목록에 영원히 남는다.
            using var legacy = SqlServerTestDatabase.TryCreate(out var reason);
            if (legacy == null) Assert.Ignore(reason);

            // v1 모양: Target 컬럼도 버전 표식도 없다.
            legacy.Execute(@"
CREATE TABLE [dbo].[DBVC_ChangeLog] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [EventType] NVARCHAR(100) NOT NULL,
    [SchemaName] NVARCHAR(128) NULL,
    [ObjectName] NVARCHAR(256) NOT NULL,
    [ObjectType] NVARCHAR(100) NOT NULL,
    [PostTime] DATETIME NOT NULL DEFAULT GETDATE(),
    [LoginName] NVARCHAR(256) NOT NULL,
    [TSQLCommand] NVARCHAR(MAX) NULL,
    [IsProcessed] BIT NOT NULL DEFAULT 0)");
            legacy.Execute(@"
INSERT INTO dbo.DBVC_ChangeLog (EventType, SchemaName, ObjectName, ObjectType, LoginName, IsProcessed)
VALUES (N'CREATE_USER', N'dbo', N'ghost_user', N'USER', N'tester', 0),
       (N'CREATE_INDEX', N'dbo', N'IX_Orphan', N'INDEX', N'tester', 0),
       (N'ALTER_TABLE', N'dbo', N'RealTable', N'TABLE', N'tester', 0)");

            new StateTracker(NewConfig()).InitializeDatabase(SqlServerTestDatabase.ServerName, legacy.Name);

            var stillOpen = legacy.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE IsProcessed = 0");
            var realOpen = legacy.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE IsProcessed = 0 AND ObjectName = N'RealTable'");
            // 이 테스트만 v1 모양으로 테이블을 직접 만들어 ALTER TABLE ... ADD 보정 경로를 태운다 -
            // 다른 테스트는 전부 현재 스크립트가 처음부터 만든 DB라 이 경로를 타지 않는다.
            var hasTargetObjectName = legacy.QueryScalar(
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.DBVC_ChangeLog') AND name = N'TargetObjectName'");
            var hasTargetObjectType = legacy.QueryScalar(
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.DBVC_ChangeLog') AND name = N'TargetObjectType'");

            Assert.Multiple(() =>
            {
                Assert.That(Convert.ToInt32(stillOpen), Is.EqualTo(1), "커밋 불가 행 둘이 닫혀야 한다");
                Assert.That(Convert.ToInt32(realOpen), Is.EqualTo(1), "커밋할 수 있는 변경까지 닫으면 안 된다");
                Assert.That(Convert.ToInt32(hasTargetObjectName), Is.EqualTo(1), "ALTER TABLE 보정으로 TargetObjectName이 추가돼야 한다");
                Assert.That(Convert.ToInt32(hasTargetObjectType), Is.EqualTo(1), "ALTER TABLE 보정으로 TargetObjectType이 추가돼야 한다");
            });
        }
    }
}
