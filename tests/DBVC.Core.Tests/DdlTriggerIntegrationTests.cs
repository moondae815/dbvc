using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

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

        /// <summary>트리거가 LoginName에 기록하는 값과 같은 함수를 같은 서버에서 부른다.</summary>
        private static string? CurrentLogin() => _db!.QueryScalar("SELECT SUSER_SNAME()") as string;

        /// <summary>트리거가 HostName에 기록하는 값과 같은 함수를 같은 서버에서 부른다.</summary>
        private static string? CurrentHost() => _db!.QueryScalar("SELECT HOST_NAME()") as string;

        private static ConfigManager NewConfig()
            => new ConfigManager(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "dbvc_cfg_" + Guid.NewGuid().ToString("N"), "mappings.json"));

        [Test]
        public void Trigger_LogsTheChange_WhenAnUnprivilegedUserRunsDdl()
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
                    LastLogId = maxId,
                    // MarkProcessed는 레코드의 작업자로 좁힌다. RefreshState가 만든
                    // 레코드에는 이 값이 늘 들어 있으므로, 손으로 만드는 여기서도 채워야
                    // 실제 경로와 같은 조건이 나간다 - 비워 두면 아무 행도 닫히지 않는다.
                    Author = CurrentLogin(),
                    HostName = CurrentHost()
                }
            });

            var open = _db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog " +
                "WHERE IsProcessed = 0 AND ObjectName IN (N'MarkedTable', N'IX_MarkedTable_Name')");

            Assert.That(Convert.ToInt32(open), Is.Zero, "테이블을 커밋하면 딸린 인덱스 행도 함께 닫혀야 한다");
        }

        [Test]
        public void MarkProcessed_LeavesTheDmlTriggerRowOpen_WhenOnlyTheParentTableIsCommitted()
        {
            // TargetObjectName은 인덱스 전용이 아니다 - DML 트리거 이벤트도 부모 테이블을 거기 남긴다.
            // 조건을 타입 없이 넓히면 테이블만 커밋했는데 트리거의 로그 행까지 닫혀, 커밋된 적 없는
            // 변경이 목록에서 조용히 사라진다. 조건은 NormalizeRow가 부모로 바꾸는 타입만 덮어야 한다.
            _db!.Execute("CREATE TABLE dbo.AuditedTable (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL)");
            _db.Execute("CREATE NONCLUSTERED INDEX IX_AuditedTable_Name ON dbo.AuditedTable (Name)");
            _db.Execute("CREATE TRIGGER dbo.trg_AuditedTable_Audit ON dbo.AuditedTable AFTER INSERT AS SET NOCOUNT ON");

            // 트리거 행이 가장 늦게 들어오므로, 그 Id까지 포함해야 조건이 실제로 시험된다.
            var maxId = Convert.ToInt64(_db.QueryScalar(
                "SELECT MAX(Id) FROM dbo.DBVC_ChangeLog WHERE ObjectName IN " +
                "(N'AuditedTable', N'IX_AuditedTable_Name', N'trg_AuditedTable_Audit')"));

            new StateTracker(NewConfig()).MarkProcessed(SqlServerTestDatabase.ServerName, _db.Name, new[]
            {
                new DBVC.Core.Models.ChangeRecord
                {
                    Schema = "dbo",
                    ObjectName = "AuditedTable",
                    ObjectType = "TABLE",
                    State = "Modified",
                    QualifiedName = "dbo.AuditedTable",
                    RelativePath = "dbo/Tables/AuditedTable.sql",
                    LastLogId = maxId,
                    // MarkProcessed는 레코드의 작업자로 좁힌다. RefreshState가 만든
                    // 레코드에는 이 값이 늘 들어 있으므로, 손으로 만드는 여기서도 채워야
                    // 실제 경로와 같은 조건이 나간다 - 비워 두면 아무 행도 닫히지 않는다.
                    Author = CurrentLogin(),
                    HostName = CurrentHost()
                }
            });

            var indexOpen = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE IsProcessed = 0 AND ObjectName = N'IX_AuditedTable_Name'"));
            var triggerOpen = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE IsProcessed = 0 AND ObjectName = N'trg_AuditedTable_Audit'"));

            Assert.Multiple(() =>
            {
                Assert.That(indexOpen, Is.Zero, "테이블을 커밋하면 딸린 인덱스 행은 닫혀야 한다");
                Assert.That(triggerOpen, Is.EqualTo(1), "커밋한 적 없는 DML 트리거의 행을 닫으면 안 된다");
            });
        }

        [Test]
        public void MarkProcessed_ClosesTheColumnRenameRow_WhenTheParentTableIsCommitted()
        {
            // 컬럼 이름 변경은 COLUMN 타입 한 행으로만 남고 테이블 행이 따로 생기지 않는다.
            // 부모로 정규화되므로 커밋도 그 행을 닫아야 한다 - 아니면 매번 다시 올라온다.
            _db!.Execute("CREATE TABLE dbo.RenamedColumnTable (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL)");
            _db.Execute("EXEC sp_rename N'dbo.RenamedColumnTable.Name', N'FullName', N'COLUMN'");

            var maxId = Convert.ToInt64(_db.QueryScalar(
                "SELECT MAX(Id) FROM dbo.DBVC_ChangeLog WHERE TargetObjectName = N'RenamedColumnTable' OR ObjectName = N'RenamedColumnTable'"));
            var recorded = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE IsProcessed = 0 " +
                "AND ObjectType = N'COLUMN' AND TargetObjectName = N'RenamedColumnTable'"));

            new StateTracker(NewConfig()).MarkProcessed(SqlServerTestDatabase.ServerName, _db.Name, new[]
            {
                new DBVC.Core.Models.ChangeRecord
                {
                    Schema = "dbo",
                    ObjectName = "RenamedColumnTable",
                    ObjectType = "TABLE",
                    State = "Modified",
                    QualifiedName = "dbo.RenamedColumnTable",
                    RelativePath = "dbo/Tables/RenamedColumnTable.sql",
                    LastLogId = maxId,
                    // MarkProcessed는 레코드의 작업자로 좁힌다. RefreshState가 만든
                    // 레코드에는 이 값이 늘 들어 있으므로, 손으로 만드는 여기서도 채워야
                    // 실제 경로와 같은 조건이 나간다 - 비워 두면 아무 행도 닫히지 않는다.
                    Author = CurrentLogin(),
                    HostName = CurrentHost()
                }
            });

            var open = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE IsProcessed = 0 AND TargetObjectName = N'RenamedColumnTable'"));

            Assert.Multiple(() =>
            {
                Assert.That(recorded, Is.EqualTo(1), "컬럼 이름 변경이 애초에 기록되지 않았다");
                Assert.That(open, Is.Zero, "부모 테이블을 커밋하면 컬럼 이름 변경 행도 닫혀야 한다");
            });
        }

        [Test]
        public void MarkProcessed_ReturnsNull_WhenTheRowsClose()
        {
            // 정상 경로에서 사유가 돌아오면 커밋마다 상자가 뜬다.
            _db!.Execute("CREATE TABLE dbo.QuietCloseTable (Id int NOT NULL PRIMARY KEY)");

            var maxId = Convert.ToInt64(_db.QueryScalar(
                "SELECT MAX(Id) FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'QuietCloseTable'"));

            var reason = new StateTracker(NewConfig()).MarkProcessed(
                SqlServerTestDatabase.ServerName, _db.Name, new[] { RecordFor("QuietCloseTable", maxId) });

            Assert.That(reason, Is.Null);
        }

        [Test]
        public void MarkProcessed_ReturnsTheReason_WhenTheUpdateCannotRun()
        {
            // 여기가 이 수정의 요점이다. 예전에는 예외를 Debug.WriteLine으로 삼켜, 커밋은 성공했는데
            // 로그가 닫히지 않은 상태를 사용자가 알 방법이 없었다 - 그 항목이 새로고침마다 되살아나고
            // 원인은 어디에도 나타나지 않았다. 없는 데이터베이스를 가리켜 접속 자체를 실패시킨다.
            var reason = new StateTracker(NewConfig()).MarkProcessed(
                SqlServerTestDatabase.ServerName,
                SqlServerTestDatabase.Prefix + "no_such_db_" + Guid.NewGuid().ToString("N"),
                new[] { RecordFor("Anything", 1) });

            Assert.That(reason, Is.Not.Null, "실패했는데 사유가 돌아오지 않았습니다");
            Assert.That(reason, Does.Contain("커밋은 성공"));
        }

        [Test]
        public void InstallScript_LetsAnUnprivilegedUserReadAndCloseTheLog()
        {
            // db_owner가 아닌 계정도 목록 조회(SELECT)와 커밋 후 로그 닫기(UPDATE)를 자기 권한으로 한다.
            // 트리거의 INSERT만 dbo로 도는 것이라, 이 GRANT가 없으면 그런 계정의 커밋이 로그를 닫지
            // 못한다.
            //
            // INSERT가 막혀 있다는 것은 여기서 확인하지 않는다 - 권한이 거부된 문장은 세션을 중지
            // 상태로 만들어 뒤따르는 REVERT까지 실패시키고, 그 접속이 풀로 돌아가 무관한 테스트를
            // 깨뜨린다. 부여한 동사 목록이 정확히 SELECT·UPDATE인지는 InstallScriptSyncTests가
            // 스크립트를 파싱해 검사하므로, 넓어지는 회귀는 그쪽에서 잡힌다.
            _db!.ExecuteInOneSession("CREATE USER dbvc_low_grant WITHOUT LOGIN");

            Assert.DoesNotThrow(() => _db.ExecuteInOneSession(
                "EXECUTE AS USER = 'dbvc_low_grant'",
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog",
                "UPDATE dbo.DBVC_ChangeLog SET IsProcessed = IsProcessed WHERE 1 = 0",
                "REVERT"), "설치 스크립트의 GRANT가 없거나 부족합니다");
        }

        /// <summary>MarkProcessed에 넘길 최소 레코드. 작업자를 채우지 않으면 아무 행도 닫히지 않는다.</summary>
        private static DBVC.Core.Models.ChangeRecord RecordFor(string name, long lastLogId) =>
            new DBVC.Core.Models.ChangeRecord
            {
                Schema = "dbo",
                ObjectName = name,
                ObjectType = "TABLE",
                State = "Modified",
                QualifiedName = "dbo." + name,
                RelativePath = "dbo/Tables/" + name + ".sql",
                LastLogId = lastLogId,
                Author = CurrentLogin(),
                HostName = CurrentHost()
            };

        [Test]
        public void Trigger_RecordsTheParentTable_ForColumnRenames()
        {
            // sp_rename은 COLUMN 이벤트 하나만 남기고 테이블 이벤트를 따로 내지 않는다.
            // 이 타입을 거르면 부모 테이블이 다시 추출되지 않아 저장소가 조용히 어긋난다.
            _db!.Execute("CREATE TABLE dbo.ColumnRenameTable (Id int NOT NULL PRIMARY KEY, Nickname nvarchar(50) NULL)");
            _db.Execute("EXEC sp_rename N'dbo.ColumnRenameTable.Nickname', N'Handle', N'COLUMN'");

            var target = _db.QueryScalar(
                "SELECT TargetObjectName FROM dbo.DBVC_ChangeLog " +
                "WHERE ObjectName = N'Nickname' AND ObjectType = N'COLUMN'");

            Assert.That(target, Is.EqualTo("ColumnRenameTable"));
        }

        [Test]
        public void InstallScript_ThrowsAndKeepsTheExistingTrigger_WhenTheInstallerCannotImpersonateDbo()
        {
            // v2 트리거는 WITH EXECUTE AS 'dbo'라 IMPERSONATE 권한이 필요하지만, 기존 트리거를 지우는
            // DROP TRIGGER ... ON DATABASE는 ALTER ANY DATABASE DDL TRIGGER면 된다. 먼저 지우고 나서
            // CREATE가 실패하면 그 데이터베이스는 변경 추적이 통째로 꺼진 채 남는다 - 이후의 모든
            // 스키마 변경이 로그 없이 지나간다. 사전 점검이 없으면 배너의 '추적기 업데이트' 한 번으로 그렇게 된다.
            using var restricted = SqlServerTestDatabase.TryCreate(out var reason);
            if (restricted == null) Assert.Ignore(reason ?? "SQL Server에 접속할 수 없습니다.");

            new StateTracker(NewConfig()).InitializeDatabase(SqlServerTestDatabase.ServerName, restricted.Name);

            restricted.ExecuteInOneSession(
                "CREATE USER dbvc_noimp WITHOUT LOGIN",
                "GRANT CREATE TABLE TO dbvc_noimp",
                "GRANT ALTER ANY DATABASE DDL TRIGGER TO dbvc_noimp",
                "GRANT ALTER ON SCHEMA::dbo TO dbvc_noimp");

            // InitializeDatabase는 자기 연결을 열므로 가장(impersonation)을 걸 자리가 없다.
            // 같은 배치 분할·같은 스크립트를 쓰되 세션만 저권한 사용자로 바꿔 실제 설치 경로를 재현한다.
            var batches = new List<string> { "EXECUTE AS USER = 'dbvc_noimp'" };
            batches.AddRange(StateTracker.SplitSqlBatches(StateTracker.ReadInstallScript()));

            var ex = Assert.Throws<SqlException>(() => restricted.ExecuteInOneUnpooledSession(batches.ToArray()));

            var stillThere = restricted.QueryScalar(
                "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND name = N'trg_DBVC_DDL_Tracker'");

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("db_owner"),
                    "사용자가 사유를 알 수 있어야 한다 - ViewChangesViewModel이 이 메시지를 그대로 보여준다");
                Assert.That(Convert.ToInt32(stillThere), Is.EqualTo(1),
                    "설치가 실패했으면 기존 추적기는 그대로 남아 있어야 한다");
            });
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
            // 부모를 모르는 인덱스·컬럼 행. 그대로 두면 목록에 영원히 남는다.
            using var legacy = SqlServerTestDatabase.TryCreate(out var reason);
            if (legacy == null) Assert.Ignore(reason ?? "SQL Server에 접속할 수 없습니다.");

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
       (N'RENAME', N'dbo', N'OrphanColumn', N'COLUMN', N'tester', 0),
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
                Assert.That(Convert.ToInt32(stillOpen), Is.EqualTo(1), "커밋 불가 행 셋이 닫혀야 한다");
                Assert.That(Convert.ToInt32(realOpen), Is.EqualTo(1), "커밋할 수 있는 변경까지 닫으면 안 된다");
                Assert.That(Convert.ToInt32(hasTargetObjectName), Is.EqualTo(1), "ALTER TABLE 보정으로 TargetObjectName이 추가돼야 한다");
                Assert.That(Convert.ToInt32(hasTargetObjectType), Is.EqualTo(1), "ALTER TABLE 보정으로 TargetObjectType이 추가돼야 한다");
            });
        }

        [Test]
        public void Trigger_RecordsTheClientHostName_WhenDdlRuns()
        {
            // 개발·테스트 DB는 공용 SQL 계정을 쓴다. LoginName이 모든 행에서 같으므로
            // 사람을 가르는 축은 접속 PC뿐이다(설계 3.9). 여기가 비면 필터가 통째로 무너진다.
            _db!.ExecuteInOneSession("CREATE PROCEDURE dbo.HostNameProbe AS SELECT 1");

            // as 캐스팅을 쓰는 이유: 컬럼이 NULL이면 ExecuteScalar가 DBNull을 돌려주는데
            // (string?) 캐스팅은 거기서 InvalidCastException으로 죽어 아래 안내 문구가 묻힌다.
            var recorded = _db.QueryScalar(
                "SELECT TOP 1 HostName FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'HostNameProbe' ORDER BY Id DESC") as string;

            Assert.That(recorded, Is.Not.Null.And.Not.Empty,
                "EXECUTE AS 'dbo' 문맥에서 HOST_NAME()이 값을 내지 못했습니다 - 필터의 축을 다시 정해야 합니다");
        }

        [Test]
        public void Trigger_RecordsTheClientNetAddress_WhenDdlRuns()
        {
            // IP는 필터에 쓰지 않는다. HostName은 클라이언트가 보내는 값이라 신뢰도가 낮아,
            // 이상한 경우를 사람이 판별할 근거로만 남긴다.
            _db!.ExecuteInOneSession("CREATE PROCEDURE dbo.ClientAddressProbe AS SELECT 1");

            var recorded = _db.QueryScalar(
                "SELECT TOP 1 ClientNetAddress FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'ClientAddressProbe' ORDER BY Id DESC") as string;

            Assert.That(recorded, Is.Not.Null.And.Not.Empty,
                "EXECUTE AS 'dbo' 문맥에서 CONNECTIONPROPERTY가 값을 내지 못했습니다");
        }

        [Test]
        public void Trigger_RecordsTheSameHostNameTheClientSees()
        {
            // 클라이언트가 SELECT HOST_NAME()으로 얻은 값과 글자 단위로 같아야 한다.
            // 다르면 필터가 전부를 걸러내 목록이 항상 빈다.
            _db!.ExecuteInOneSession("CREATE PROCEDURE dbo.HostNameMatchProbe AS SELECT 1");

            var fromTrigger = _db.QueryScalar(
                "SELECT TOP 1 HostName FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'HostNameMatchProbe' ORDER BY Id DESC") as string;
            var fromClient = _db.QueryScalar("SELECT HOST_NAME()") as string;

            Assert.That(fromTrigger, Is.EqualTo(fromClient));
        }

        [Test]
        public void RefreshState_ExcludesOtherWorkstationsChanges_WhenNotIncludingAllAuthors()
        {
            // 같은 공용 계정으로 서로 다른 PC에서 작업하는 상황을 Workstation ID로 흉내낸다.
            // 이 테스트가 이 계획 전체의 핵심이다 - 여기가 통과하지 않으면 나머지는 의미가 없다.
            _db!.ExecuteWithWorkstationId("OTHER-PC", "CREATE PROCEDURE dbo.OtherPcProbe AS SELECT 1");
            // 내 쪽에서도 하나 만든다. 이것이 없으면 필터가 전부를 걸러내 목록이 항상 비어도
            // 아래 Does.Not.Contain이 통과해 버려, 테스트가 아무것도 보장하지 않는다.
            _db.Execute("CREATE PROCEDURE dbo.MyPcProbe AS SELECT 1");

            // RefreshState는 매핑이 없으면 아무것도 읽지 않고 false를 낸다. Git 쪽은 저장소가
            // 아니어도 빈 상태를 돌려주므로, 여기서는 경로가 가리키는 곳이 실제 저장소일 필요가 없다.
            var config = NewConfig();
            var repoPath = Path.Combine(Path.GetTempPath(), "dbvc_repo_" + Guid.NewGuid().ToString("N"));
            config.AddMapping(SqlServerTestDatabase.ServerName, _db.Name, repoPath);
            var tracker = new StateTracker(config);

            Assert.That(tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: false),
                Is.True, "매핑이 있으면 갱신에 성공해야 한다");
            var mine = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

            Assert.That(mine.Select(c => c.ObjectName), Does.Contain("MyPcProbe"), "내 변경은 남아야 한다");
            Assert.That(mine.Select(c => c.ObjectName), Does.Not.Contain("OtherPcProbe"));

            tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
            var all = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

            Assert.That(all.Select(c => c.ObjectName), Does.Contain("OtherPcProbe"));
        }

        /// <summary>
        /// 작업자 필터가 실제로 새던 자리. 좁히기는 로그 쪽에서 제대로 돌지만, 추출이 작업자를
        /// 가리지 않으므로 남의 객체도 .sql이 써지고 그 파일이 Git에서 더럽게 보인다. 그러면
        /// "DDL 로그에 없지만 Git에서 변경된 파일"을 구제하는 폴백이 방금 걸러낸 것을 도로 넣었다.
        ///
        /// 위의 RefreshState_ExcludesOtherWorkstationsChanges는 저장소가 아닌 경로를 매핑해
        /// Git 상태가 늘 비어 있으므로 이 경로를 밟지 못한다. 여기서는 진짜 저장소를 만들고
        /// 남의 객체 파일을 실제로 놓아 둔다.
        /// </summary>
        [Test]
        public void RefreshState_StillExcludesOtherWorkstationsChanges_WhenTheirFileIsDirtyInGit()
        {
            _db!.ExecuteWithWorkstationId("OTHER-PC", "CREATE PROCEDURE dbo.OtherPcDirtyProbe AS SELECT 1");
            // 내 것이 하나는 남아야 한다. 필터가 전부를 걸러내도 아래 Does.Not.Contain이
            // 통과해 버리면 테스트가 아무것도 보장하지 않는다.
            _db.Execute("CREATE PROCEDURE dbo.MyPcDirtyProbe AS SELECT 1");

            var repoPath = Path.Combine(Path.GetTempPath(), "dbvc_repo_" + Guid.NewGuid().ToString("N"));
            try
            {
                LibGit2Sharp.Repository.Init(repoPath);

                // 새로고침이 남의 객체까지 추출한 뒤의 모습이다. 미추적 파일이라 Git이 더럽다고 본다.
                var relative = ObjectPathConvention.GetRelativePath("dbo", "PROCEDURE", "OtherPcDirtyProbe");
                var full = Path.Combine(repoPath, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "CREATE OR ALTER PROCEDURE dbo.OtherPcDirtyProbe AS SELECT 1");

                var config = NewConfig();
                config.AddMapping(SqlServerTestDatabase.ServerName, _db.Name, repoPath);
                var tracker = new StateTracker(config);

                tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: false);
                var mine = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

                Assert.That(mine.Select(c => c.ObjectName), Does.Contain("MyPcDirtyProbe"),
                    "내 변경은 남아야 한다");
                Assert.That(mine.Select(c => c.ObjectName), Does.Not.Contain("OtherPcDirtyProbe"),
                    "파일이 더럽다는 이유로 남의 변경이 목록에 돌아오면 필터가 아무것도 막지 못한다");

                tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
                var all = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

                Assert.That(all.Select(c => c.ObjectName), Does.Contain("OtherPcDirtyProbe"),
                    "전체 보기에서는 보여야 한다");
            }
            finally
            {
                TryDeleteRepo(repoPath);
            }
        }

        /// <summary>
        /// 삭제는 새로고침 한 번으로 끝나지 않는다. WorkingTreeCleaner가 첫 새로고침 끝에
        /// 그 .sql을 지우므로, 두 번째 새로고침에서는 파일이 이미 없다. 존재 확인을
        /// File.Exists로만 하면 그때 이 항목이 목록에서 사라지고 - 선택할 수 없으니
        /// 커밋에도 담기지 않아 - 저장소에는 없어진 객체의 .sql이 영원히 남는다.
        ///
        /// Git은 그 파일을 "삭제됨"으로 보고 있다. 근거를 파일 시스템이 아니라 Git에서 받아야 한다.
        /// </summary>
        [Test]
        public void RefreshState_KeepsTheDeletion_WhenTheCleanerAlreadyRemovedTheFile()
        {
            _db!.Execute("CREATE PROCEDURE dbo.DropTwiceProbe AS SELECT 1");
            _db.Execute("DROP PROCEDURE dbo.DropTwiceProbe");

            var repoPath = Path.Combine(Path.GetTempPath(), "dbvc_repo_" + Guid.NewGuid().ToString("N"));
            try
            {
                var relative = ObjectPathConvention.GetRelativePath("dbo", "PROCEDURE", "DropTwiceProbe");
                var full = Path.Combine(repoPath, relative.Replace('/', Path.DirectorySeparatorChar));
                CommitFile(repoPath, full, "CREATE OR ALTER PROCEDURE dbo.DropTwiceProbe AS SELECT 1");

                var config = NewConfig();
                config.AddMapping(SqlServerTestDatabase.ServerName, _db.Name, repoPath);
                var tracker = new StateTracker(config);

                tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
                Assert.That(
                    tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name)
                        .SingleOrDefault(c => c.ObjectName == "DropTwiceProbe")?.State,
                    Is.EqualTo("Deleted"), "첫 새로고침에서 삭제로 떠야 한다");

                // WorkingTreeCleaner가 하는 일이다. 커밋하지 않은 채 다시 새로고침하는 것은
                // 흔한 흐름이다 - 커밋을 취소했거나 실패했을 때가 그렇다.
                File.Delete(full);

                tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
                var second = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name)
                    .SingleOrDefault(c => c.ObjectName == "DropTwiceProbe");

                Assert.That(second, Is.Not.Null,
                    "삭제가 목록에서 사라지면 선택할 수 없고, 커밋에 담기지 않아 저장소에 .sql이 영영 남는다");
                Assert.That(second!.State, Is.EqualTo("Deleted"));
            }
            finally
            {
                TryDeleteRepo(repoPath);
            }
        }

        /// <summary>
        /// 이름을 접으면 화면의 이름과 로그에 저장된 이름이 달라진다. MarkProcessed가 화면의
        /// 이름으로만 좁히면 옛 이름으로 기록된 행이 영원히 열린 채 남아, 매번 새로고침마다
        /// 같은 항목이 다시 올라온다 - 이번에는 비교창이 빈 유령이 아니라 살아 있는 테이블의
        /// "수정"으로. 게다가 .sql은 이미 같아졌으니 다시 커밋해서 지울 수도 없다.
        /// </summary>
        [Test]
        public void MarkProcessed_ClosesTheRowsRecordedUnderTheOldName_AfterARenameWasFolded()
        {
            _db!.Execute("CREATE TABLE dbo.CloseFoldedProbe (Id int NOT NULL, Name nvarchar(50) NULL)");

            _db.ExecuteInOneSession(
                "CREATE TABLE dbo.Tmp_CloseFoldedProbe (Id int NOT NULL, Name nvarchar(200) NULL) ON [PRIMARY]",
                "ALTER TABLE dbo.Tmp_CloseFoldedProbe SET (LOCK_ESCALATION = TABLE)",
                "DROP TABLE dbo.CloseFoldedProbe",
                "EXECUTE sp_rename N'dbo.Tmp_CloseFoldedProbe', N'CloseFoldedProbe', 'OBJECT'");

            var config = NewConfig();
            config.AddMapping(SqlServerTestDatabase.ServerName, _db.Name,
                Path.Combine(Path.GetTempPath(), "dbvc_repo_" + Guid.NewGuid().ToString("N")));
            var tracker = new StateTracker(config);

            tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
            var records = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name)
                .Where(c => c.ObjectName == "CloseFoldedProbe").ToList();
            Assert.That(records, Has.Count.EqualTo(1), "접힌 뒤에는 항목이 하나여야 한다");

            tracker.MarkProcessed(SqlServerTestDatabase.ServerName, _db.Name, records);

            var stillOpen = _db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE IsProcessed = 0 AND ObjectName = N'Tmp_CloseFoldedProbe'");

            Assert.That(stillOpen, Is.EqualTo(0),
                "옛 이름으로 기록된 행이 닫히지 않으면 같은 항목이 매번 다시 올라오고, 지울 방법이 없다");
        }

        /// <summary>
        /// 남이 만진 행이 내 커밋에 이미 담긴 내용과 같아지는 경우. 공용 계정 환경에서 흔하다 -
        /// 남이 고친 위에 내가 또 고치고 내가 먼저 커밋하면, 남의 행이 가리키는 코드는 이미
        /// 저장소에 들어가 있다.
        ///
        /// 그때 스테이징할 차이가 없어 커밋이 만들어지지 않는데, 그것을 "커밋할 수 없다"와
        /// 같이 다루면 그 행이 영원히 열린 채 남는다. 전체 보기에 계속 뜨고, 다시 커밋해도
        /// 또 차이가 없어 아무 일도 일어나지 않아 사용자가 지울 방법이 없다.
        /// </summary>
        [Test]
        public void MarkProcessed_ClosesTheOtherAuthorsRow_WhenTheRepositoryAlreadyHasThatCode()
        {
            _db!.Execute("CREATE PROCEDURE dbo.LeftoverProbe AS SELECT 1");
            _db.ExecuteWithWorkstationId("OTHER-PC", "ALTER PROCEDURE dbo.LeftoverProbe AS SELECT 2");
            // 내가 그 위에 또 고친다. 이 상태로 내가 커밋하면 남의 변경까지 저장소에 담긴다.
            _db.Execute("ALTER PROCEDURE dbo.LeftoverProbe AS SELECT 3");

            var repoPath = Path.Combine(Path.GetTempPath(), "dbvc_repo_" + Guid.NewGuid().ToString("N"));
            try
            {
                var relative = ObjectPathConvention.GetRelativePath("dbo", "PROCEDURE", "LeftoverProbe");
                var full = Path.Combine(repoPath, relative.Replace('/', Path.DirectorySeparatorChar));
                CommitFile(repoPath, full, "CREATE OR ALTER PROCEDURE dbo.LeftoverProbe AS SELECT 3");

                var config = NewConfig();
                config.AddMapping(SqlServerTestDatabase.ServerName, _db.Name, repoPath);
                var tracker = new StateTracker(config);
                var git = new GitManager(config);

                // 내 보기로 먼저 커밋한다. 이래야 남의 행만 남아 실제 순서와 같아진다.
                tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: false);
                tracker.MarkProcessed(SqlServerTestDatabase.ServerName, _db.Name,
                    tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name)
                        .Where(c => c.ObjectName == "LeftoverProbe").ToList());

                // 전체 보기. 이제 남의 행만 남아 있고, 그 코드는 이미 저장소에 들어가 있다.
                tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
                var records = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name)
                    .Where(c => c.ObjectName == "LeftoverProbe").ToList();
                Assert.That(records, Has.Count.EqualTo(1), "이 테스트의 전제가 성립해야 한다");

                var result = git.CommitChanges(SqlServerTestDatabase.ServerName, _db.Name, "이미 같음",
                    records.Select(r => r.RelativePath).ToList());
                Assert.That(result, Is.EqualTo(GitCommitResult.NothingToCommit),
                    "저장소가 이미 그 코드를 갖고 있으므로 담을 것이 없어야 한다");

                tracker.MarkProcessed(SqlServerTestDatabase.ServerName, _db.Name, records);

                tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
                Assert.That(
                    tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name)
                        .Select(c => c.ObjectName),
                    Does.Not.Contain("LeftoverProbe"),
                    "닫히지 않으면 전체 보기에 영원히 남고, 다시 커밋해도 또 차이가 없어 지울 수 없다");
            }
            finally
            {
                TryDeleteRepo(repoPath);
            }
        }

        /// <summary>커밋된 파일이 있는 저장소를 만든다. 미추적 파일은 지워도 Git이 "삭제됨"으로 보지 않는다.</summary>
        private static void CommitFile(string repoPath, string fullPath, string content)
        {
            LibGit2Sharp.Repository.Init(repoPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);

            using var repo = new LibGit2Sharp.Repository(repoPath);
            LibGit2Sharp.Commands.Stage(repo, "*");
            var who = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("초기", who, who, new LibGit2Sharp.CommitOptions());
        }

        /// <summary>.git 안에는 읽기 전용 파일이 있어 그냥 지우면 실패한다. 지우지 못해도 테스트를 깨지 않는다.</summary>
        private static void TryDeleteRepo(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }

        /// <summary>
        /// SSMS 테이블 디자이너로 열 형식을 바꾸면 SQL Server가 테이블을 재작성한다 —
        /// Tmp_ 테이블을 만들고, 데이터를 옮기고, 원본을 DROP한 뒤, 이름을 바꾼다.
        /// 그러면 원본 이름의 최신 이벤트가 DROP_TABLE이라 살아 있는 테이블이 삭제로 뜨고,
        /// 커밋하면 WorkingTreeCleaner가 저장소에서 그 .sql을 지운다.
        ///
        /// PK를 더할 때는 뒤에 ALTER가 하나 더 붙어 우연히 가려지므로, 그것이 붙지 않는
        /// 열 형식 변경으로 재현한다. 아래 스크립트는 디자이너가 실제로 내보내는 것이다.
        /// </summary>
        [Test]
        public void RefreshState_ReportsTheLiveTableAsModified_WhenTheDesignerRebuiltIt()
        {
            _db!.Execute("CREATE TABLE dbo.RebuiltProbe (Id int NOT NULL, Name nvarchar(50) NULL)");

            _db.ExecuteInOneSession(
                @"CREATE TABLE dbo.Tmp_RebuiltProbe
                    (
                    Id int NOT NULL,
                    Name nvarchar(200) NULL
                    )  ON [PRIMARY]",
                "ALTER TABLE dbo.Tmp_RebuiltProbe SET (LOCK_ESCALATION = TABLE)",
                @"IF EXISTS(SELECT * FROM dbo.RebuiltProbe)
                     EXEC('INSERT INTO dbo.Tmp_RebuiltProbe (Id, Name) SELECT Id, Name FROM dbo.RebuiltProbe WITH (HOLDLOCK TABLOCKX)')",
                "DROP TABLE dbo.RebuiltProbe",
                "EXECUTE sp_rename N'dbo.Tmp_RebuiltProbe', N'RebuiltProbe', 'OBJECT'");

            // 매핑된 폴더는 비어 있다. 사라진 Tmp_ 이름은 DB에도 저장소에도 없으므로 걷힌다.
            var config = NewConfig();
            config.AddMapping(SqlServerTestDatabase.ServerName, _db.Name,
                Path.Combine(Path.GetTempPath(), "dbvc_repo_" + Guid.NewGuid().ToString("N")));
            var tracker = new StateTracker(config);

            Assert.That(tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true),
                Is.True);
            var changes = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

            var rebuilt = changes.SingleOrDefault(c => c.ObjectName == "RebuiltProbe");
            Assert.That(rebuilt, Is.Not.Null, "재작성된 테이블은 목록에 있어야 한다");
            Assert.That(rebuilt!.State, Is.EqualTo("Modified"),
                "살아 있는 테이블이 삭제로 뜨면 커밋 시점에 저장소에서 .sql이 지워진다");

            Assert.That(changes.Select(c => c.ObjectName), Does.Not.Contain("Tmp_RebuiltProbe"),
                "존재한 적 없는 이름이 목록에 남으면 비교창이 빈 채로 뜬다");
        }

        /// <summary>
        /// sp_rename은 ObjectName에 옛 이름만 남긴다. 접지 않으면 새 이름이 로그 어디에도
        /// 없어 그 객체가 영영 추출되지 않는다 - 목록에서 사라지는 것이 아니라, 바뀐 코드가
        /// 저장소에 반영되지 않는 쪽이라 눈에 띄지 않는다.
        ///
        /// 추출 대상 목록(GetChangedObjectNames)까지 보는 이유가 그것이다. 화면만 고치면
        /// 목록은 새 이름을 보여 주는데 SMO는 옛 이름을 찾는다.
        /// </summary>
        [Test]
        public void Rename_MovesTheChangeToTheNewName_InBothTheListAndTheExtractionTargets()
        {
            _db!.Execute("CREATE PROCEDURE dbo.RenameProbeOld AS SELECT 1");
            _db.Execute("EXEC sp_rename N'dbo.RenameProbeOld', N'RenameProbeNew', 'OBJECT'");

            var config = NewConfig();
            config.AddMapping(SqlServerTestDatabase.ServerName, _db.Name,
                Path.Combine(Path.GetTempPath(), "dbvc_repo_" + Guid.NewGuid().ToString("N")));
            var tracker = new StateTracker(config);

            var targets = tracker.GetChangedObjectNames(SqlServerTestDatabase.ServerName, _db.Name);
            Assert.That(targets, Does.Contain("dbo.RenameProbeNew"),
                "새 이름이 추출 대상에 없으면 바뀐 코드가 저장소에 반영되지 않는다");

            tracker.RefreshState(SqlServerTestDatabase.ServerName, _db.Name, includeAllAuthors: true);
            var changes = tracker.GetPendingChanges(SqlServerTestDatabase.ServerName, _db.Name);

            Assert.That(changes.Select(c => c.ObjectName), Does.Contain("RenameProbeNew"));
            Assert.That(changes.Select(c => c.ObjectName), Does.Not.Contain("RenameProbeOld"),
                "옛 이름은 DB에도 저장소에도 없으므로 걷혀야 한다");
        }
    }
}
