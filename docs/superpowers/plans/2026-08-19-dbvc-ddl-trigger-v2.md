# DDL 트리거 계약 v2 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DDL 트리거가 남의 DDL을 막지 않게 하고, 인덱스 변경이 부모 테이블을 갱신하게 하며, 스크립팅할 수 없는 이벤트를 목록에서 몰아낸다.

**Architecture:** 판단을 두 곳으로 나눈다 — 기록할지 말지는 트리거(SQL)가, 기록된 것을 무엇으로 볼지는 Core(C#)가 정한다. 트리거에는 `WITH EXECUTE AS 'dbo'`·화이트리스트·`Target*` 컬럼만 더하고, 인덱스를 부모 테이블로 옮기는 해석은 `StateTracker`가 로그를 읽는 입구 한 곳에서 한다. 스키마 버전을 확장 속성으로 심어, 이미 설치된 데이터베이스는 도구 창의 배너로 재설치를 안내한다.

**Tech Stack:** .NET Standard 2.0 / .NET Framework 4.8 (Core), WPF MVVM (Vsix), T-SQL, NUnit 4 + Moq, `Microsoft.Data.SqlClient 5.1.5`

**Spec:** `docs/superpowers/specs/2026-08-19-dbvc-ddl-trigger-v2-design.md`

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지·알림·버튼·ToolTip 포함. Core는 상태를 영어 식별자(`Added`/`Modified`/`Deleted`)로 다루고 화면 계층에서만 한국어로 옮긴다.
- 주석은 **"왜"만** 적는다. 한국어 평서문.
- 커밋 메시지는 한국어 명령형 현재시제 + 스코프: `feat(core): ...`, `fix(vsix): ...`, `docs: ...`.
- 테스트 이름은 영어 `Method_Result_WhenCondition` 형태다.
- TDD: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0` 고정. 테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다.
- 무거운 작업(DB 왕복·네트워크)은 `IBackgroundScheduler`로 UI 스레드 밖에서 돌린다. `ObjectExplorerConnectionSource`만 예외로 UI 스레드에서 부른다.
- 스키마 버전 상수: `StateTracker.RequiredSchemaVersion = 2`. 설치 스크립트의 `DBVC_SchemaVersion` 값과 언제나 같아야 한다.
- 통합 테스트는 `localhost` SQL Server에 접속되지 않으면 **실패가 아니라 Skip**이다(`Assert.Ignore`).
- 이 계획은 **UTF-16 인코딩 문제를 다루지 않는다.** 별도 스펙이다.

## 파일 구조

**수정**
- `src/DBVC.Database/InstallTrigger.sql` — 트리거 계약 v2 전부(권한·SET·화이트리스트·`Target*` 컬럼·버전 표식·마이그레이션)
- `src/DBVC.Core/Models/ChangeRecord.cs` — `ChangeLogRow`에 `TargetObjectName`/`TargetObjectType`
- `src/DBVC.Core/ObjectPathConvention.cs` — SMO 타입 사전과 DDL 이벤트 타입 사전을 가른다
- `src/DBVC.Core/StateTracker.cs` — 버전 조회, 입구 정규화, `MarkProcessed` 조건 확장
- `src/DBVC.Core/Abstractions.cs` — `IStateTracker.IsInitialized` → `GetInstalledVersion`
- `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` — 구버전 판정·안내·업데이트 명령, 초기화를 백그라운드로
- `src/DBVC.Vsix/UI/ViewChangesControl.xaml` — 구버전 안내 한 줄
- `src/DBVC.Vsix/source.extension.vsixmanifest` — 0.2.6
- `README.md`, `docs/setup-checklist.md`

**생성**
- `tests/DBVC.Core.Tests/SqlServerTestDatabase.cs` — 통합 테스트용 임시 DB 생성·정리 헬퍼
- `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs` — 트리거 SQL의 유일한 검증 수단
- `tests/DBVC.Core.Tests/InstallScriptSyncTests.cs` — SQL의 타입 목록·버전과 C# 상수가 어긋나지 않게 고정

**수정(테스트)**
- `tests/DBVC.Core.Tests/StateTrackerTests.cs`
- `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`
- `tests/DBVC.Vsix.Tests/Services/BackgroundSchedulerWiringTests.cs`
- `tests/DBVC.Vsix.Tests/PackageTests.cs`

---

### Task 1: 통합 테스트 기반과 트리거 권한 결함

**Files:**
- Create: `tests/DBVC.Core.Tests/SqlServerTestDatabase.cs`
- Create: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`
- Modify: `src/DBVC.Database/InstallTrigger.sql`
- Modify: `tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs`

**Interfaces:**
- Consumes: `StateTracker.InitializeDatabase(server, database)`, `SqlConnectionFactory.BuildWindows(server, database)` (모두 기존 공개 API)
- Produces: `SqlServerTestDatabase` — `static SqlServerTestDatabase? TryCreate(out string? skipReason)`, 상수 `ServerName`·`Prefix`, 속성 `Name`, 메서드 `Execute(string sql)`, `ExecuteInOneSession(params string[])`, `QueryScalar(string sql)`, `Open()`, `Dispose()`. 이후 모든 통합 테스트 태스크가 쓴다.

- [ ] **Step 1: 임시 DB 헬퍼를 만든다**

`tests/DBVC.Core.Tests/SqlServerTestDatabase.cs`:

```csharp
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
```

- [ ] **Step 2: 저권한 사용자 회귀 테스트를 쓴다**

`tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`:

```csharp
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
    }
}
```

- [ ] **Step 3: 테스트를 돌려 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: FAIL — `SqlException: 트리거를 실행하는 동안 오류가 발생했습니다` (오류 3616). 로컬에 SQL Server가 없으면 Skip으로 뜨며, 그때는 이 태스크를 검증할 수 없으므로 SQL Server를 켠 뒤 진행한다.

- [ ] **Step 4: 스크립트 첫머리에 SET 옵션을 박는다**

`src/DBVC.Database/InstallTrigger.sql` 맨 위(주석 다음, 첫 `IF NOT EXISTS` 앞)에 넣는다:

```sql
-- 트리거는 이 두 옵션을 생성 시점 값으로 저장하고, 본문의 EVENTDATA().value()가 그것이 ON이어야
-- 동작한다. QUOTED_IDENTIFIER가 기본 OFF인 클라이언트(sqlcmd)로 설치하면 이 데이터베이스의
-- 모든 DDL이 오류 1934 -> 3616으로 실패한다. 클라이언트 기본값에 기대지 않는다.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
```

- [ ] **Step 5: 트리거를 dbo 권한으로 실행하고 CATCH를 지운다**

같은 파일의 `CREATE TRIGGER` 블록을 통째로 아래로 바꾼다:

```sql
CREATE TRIGGER [trg_DBVC_DDL_Tracker]
ON DATABASE
-- 로깅 INSERT를 dbo 권한으로 돌린다. 사용자 권한으로 돌리면 ChangeLog에 쓸 수 없는 사용자의
-- DDL이 통째로 실패한다 - 트리거 안의 오류는 트랜잭션을 uncommittable로 만들어, CATCH로 삼켜도
-- SQL Server가 오류 3616으로 배치를 중단하고 롤백하기 때문이다. 그래서 CATCH도 두지 않는다:
-- 트리거 안의 오류를 무해하게 만드는 방법은 없고, 삼키는 척하는 코드는 잘못된 안심만 남긴다.
WITH EXECUTE AS 'dbo'
FOR DDL_DATABASE_LEVEL_EVENTS
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EventData XML = EVENTDATA();

    DECLARE @ObjectName NVARCHAR(256) = @EventData.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(256)');

    -- DBVC 자체 테이블/트리거에 대한 DDL은 사용자 변경이 아니므로 기록하지 않는다.
    IF @ObjectName IS NULL OR @ObjectName IN (N'DBVC_ChangeLog', N'trg_DBVC_DDL_Tracker')
        RETURN;

    INSERT INTO [dbo].[DBVC_ChangeLog] (
        [EventType],
        [SchemaName],
        [ObjectName],
        [ObjectType],
        [PostTime],
        [LoginName],
        [TSQLCommand],
        [IsProcessed]
    )
    VALUES (
        @EventData.value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(100)'),
        @EventData.value('(/EVENT_INSTANCE/SchemaName)[1]', 'NVARCHAR(128)'),
        @ObjectName,
        @EventData.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(100)'),
        GETDATE(),
        @EventData.value('(/EVENT_INSTANCE/LoginName)[1]', 'NVARCHAR(256)'),
        @EventData.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'NVARCHAR(MAX)'),
        0
    );
END;
GO
```

- [ ] **Step 6: 테스트가 통과하는지 본다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: PASS

- [ ] **Step 7: 기존 통합 픽스처도 같은 헬퍼를 쓰게 한다**

`tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs`가 임시 DB를 직접 만들고 있어 정리에 실패하면 그대로 남는다 — 지금 localhost에 6개가 쌓여 있다. 헬퍼로 옮기면 다음 실행이 남은 것을 치운다.

`CreateTestDatabase`의 `try` 블록에서 연결 생성과 `CREATE DATABASE`, `USE`를 지우고 아래로 바꾼다. 시드 DDL 문장들(`CREATE TABLE dbo.Users ...`부터 `EXEC sp_addextendedproperty ...`까지)은 문자열 그대로 두고 실행 방법만 바꾼다:

```csharp
        private static SqlServerTestDatabase? _testDatabase;

        [OneTimeSetUp]
        public void CreateTestDatabase()
        {
            _testDatabase = SqlServerTestDatabase.TryCreate(out _skipReason);
            if (_testDatabase == null) return;

            // EnumerateTargets의 여러 갈래를 한 번에 지나가도록 타입을 섞는다.
            _testDatabase.ExecuteInOneSession(
                "CREATE TABLE dbo.Users (Id int IDENTITY(1,1) PRIMARY KEY, Name nvarchar(100) NOT NULL)",
                "CREATE VIEW dbo.vUsers AS SELECT Id, Name FROM dbo.Users",
                "CREATE PROCEDURE dbo.usp_GetUser @Id int AS SELECT Id, Name FROM dbo.Users WHERE Id = @Id",
                "CREATE FUNCTION dbo.fn_Double(@n int) RETURNS int AS BEGIN RETURN @n * 2 END",
                "CREATE TRIGGER dbo.trg_Users_Ins ON dbo.Users AFTER INSERT AS BEGIN SET NOCOUNT ON END",
                // 컬럼 정의만으로는 드러나지 않는 것들이다. 스크립팅 옵션이 꺼지면 조용히 사라진다.
                "ALTER TABLE dbo.Users ADD CreatedAt datetime2(7) NOT NULL " +
                "CONSTRAINT DF_Users_CreatedAt DEFAULT sysutcdatetime()",
                "CREATE NONCLUSTERED INDEX IX_Users_Name ON dbo.Users (Name)",
                "EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'사용자', " +
                "@level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'Users'");

            _database = _testDatabase.Name;
        }

        [OneTimeTearDown]
        public void DropTestDatabase() => _testDatabase?.Dispose();
```

`ServerName` 상수는 그대로 두고(`"localhost"`로 값이 같다), 나머지 테스트 본문은 손대지 않는다. `Execute(SqlConnection, string)` 헬퍼가 더 이상 쓰이지 않으면 지운다.

- [ ] **Step 8: 기존 테스트가 깨지지 않았는지 본다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS (전체)

- [ ] **Step 9: 남은 테스트 DB가 정리되는지 확인한다**

Run: `sqlcmd -S localhost -E -I -W -Q "SELECT name, create_date FROM sys.databases WHERE name LIKE 'DBVC_ITest%'"`
Expected: 방금 실행이 만든 것 외에 한 시간 넘게 묵은 항목이 없다. (실행 직후라면 정리 대상이 아직 한 시간 조건에 걸리지 않을 수 있다 — 그때는 다음 실행에서 사라진다.)

- [ ] **Step 10: 커밋**

```bash
git add src/DBVC.Database/InstallTrigger.sql tests/DBVC.Core.Tests/SqlServerTestDatabase.cs tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs tests/DBVC.Core.Tests/SmoManagerIntegrationTests.cs
git commit -m "fix(db): DDL 트리거가 남의 DDL을 막지 않게 한다"
```

---

### Task 2: 기록할 타입만 기록한다

**Files:**
- Modify: `src/DBVC.Core/ObjectPathConvention.cs`
- Modify: `src/DBVC.Database/InstallTrigger.sql`
- Create: `tests/DBVC.Core.Tests/InstallScriptSyncTests.cs`
- Modify: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1의 `SqlServerTestDatabase`, `StateTracker.ReadInstallScript()` (기존 `internal static string`)
- Produces: `ObjectPathConvention.DdlEventObjectTypes` — `internal static IReadOnlyCollection<string>`. Task 7의 마이그레이션 검증이 다시 쓴다.

- [ ] **Step 1: 유령 이벤트가 기록되지 않는다는 테스트를 쓴다**

`DdlTriggerIntegrationTests.cs`에 더한다:

```csharp
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
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~Trigger_DoesNotLogEvents"`
Expected: FAIL — `Expected: 0 But was: 2`

- [ ] **Step 3: DDL 이벤트 타입을 이름 있는 자리로 뺀다**

`src/DBVC.Core/ObjectPathConvention.cs`에서 `FolderByObjectType` 하나로 뭉쳐 있던 사전을 둘로 가른다. 지금은 SMO 타입명과 DDL 이벤트 타입명이 섞여 있어 어느 쪽이 트리거와 맞춰야 할 목록인지 코드가 말해 주지 않는다.

```csharp
        /// <summary>SMO가 내놓는 타입명. 추출 경로가 쓴다.</summary>
        private static readonly Dictionary<string, string> SmoFolderByObjectType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Table"] = "Tables",
            ["View"] = "Views",
            ["StoredProcedure"] = "StoredProcedures",
            ["UserDefinedFunction"] = "Functions",
            ["Trigger"] = "Triggers",
            ["UserDefinedType"] = "Types",
            ["UserDefinedDataType"] = "Types",
            ["UserDefinedTableType"] = "TableTypes",
            ["Sequence"] = "Sequences",
            ["Synonym"] = "Synonyms"
        };

        /// <summary>
        /// DDL 트리거 EVENTDATA의 ObjectType 값. <b>설치 스크립트의 DBVC_TRACKED_TYPES와 같은 목록이어야
        /// 하며</b>, 어긋나면 InstallScriptSyncTests가 죽는다 — 트리거가 기록하지 않는 타입을 여기 두면
        /// 화면 코드가 영원히 오지 않는 값을 기다리고, 반대면 파일이 없는 항목이 목록에 뜬다.
        /// </summary>
        private static readonly Dictionary<string, string> DdlFolderByObjectType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TABLE"] = "Tables",
            ["VIEW"] = "Views",
            ["PROCEDURE"] = "StoredProcedures",
            ["SQL_STORED_PROCEDURE"] = "StoredProcedures",
            ["FUNCTION"] = "Functions",
            ["SQL_SCALAR_FUNCTION"] = "Functions",
            ["SQL_TABLE_VALUED_FUNCTION"] = "Functions",
            ["SQL_INLINE_TABLE_VALUED_FUNCTION"] = "Functions",
            ["TRIGGER"] = "Triggers",
            ["SQL_TRIGGER"] = "Triggers",
            ["TYPE"] = "Types",
            ["TABLE_TYPE"] = "TableTypes",
            ["SEQUENCE OBJECT"] = "Sequences",
            ["SEQUENCE_OBJECT"] = "Sequences",
            ["SEQUENCE"] = "Sequences",
            ["SYNONYM"] = "Synonyms"
        };

        /// <summary>설치 스크립트의 화이트리스트와 대조되는 목록. INDEX는 여기 없다 — 독립 객체로 저장되지 않고 부모 테이블로 정규화된다.</summary>
        internal static IReadOnlyCollection<string> DdlEventObjectTypes => DdlFolderByObjectType.Keys.ToList();
```

`GetFolderName`을 두 사전을 모두 보도록 바꾼다:

```csharp
        public static string GetFolderName(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return UnknownFolder;
            var key = objectType!.Trim();
            if (SmoFolderByObjectType.TryGetValue(key, out var smoFolder)) return smoFolder;
            return DdlFolderByObjectType.TryGetValue(key, out var ddlFolder) ? ddlFolder : UnknownFolder;
        }
```

- [ ] **Step 4: 트리거에 화이트리스트를 넣는다**

`InstallTrigger.sql`의 트리거 본문에서 `IF @ObjectName IS NULL ...` 블록 **아래**에 넣는다:

```sql
    DECLARE @ObjectType NVARCHAR(100) = @EventData.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(100)');

    -- DBVC_TRACKED_TYPES: ObjectPathConvention.DdlEventObjectTypes + INDEX와 같아야 한다.
    -- InstallScriptSyncTests가 이 목록을 읽어 대조하므로 형식(따옴표 붙은 값 나열)을 바꾸지 말 것.
    -- 여기서 거르지 않으면 사용자·권한 이벤트가 파일 없는 항목으로 목록에 남는다.
    IF @ObjectType NOT IN (N'TABLE', N'VIEW', N'PROCEDURE', N'SQL_STORED_PROCEDURE',
        N'FUNCTION', N'SQL_SCALAR_FUNCTION', N'SQL_TABLE_VALUED_FUNCTION',
        N'SQL_INLINE_TABLE_VALUED_FUNCTION', N'TRIGGER', N'SQL_TRIGGER', N'TYPE',
        N'TABLE_TYPE', N'SEQUENCE OBJECT', N'SEQUENCE_OBJECT', N'SEQUENCE', N'SYNONYM',
        N'INDEX')
        RETURN;
```

INSERT문의 `ObjectType` 자리를 `@ObjectType`으로 바꾼다(같은 값을 두 번 파싱하지 않는다).

- [ ] **Step 5: 동기화 테스트를 쓴다**

`tests/DBVC.Core.Tests/InstallScriptSyncTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 설치 스크립트(SQL)와 Core(C#)에 같은 목록이 두 벌 있다. 한쪽만 고치면 조용히 어긋나
    /// 파일 없는 항목이 목록에 뜨거나 변경이 통째로 감지되지 않는다. 여기서 죽게 만든다.
    /// </summary>
    [TestFixture]
    public class InstallScriptSyncTests
    {
        /// <summary>
        /// 표식이 붙은 지점부터 다음 세미콜론까지를 한 덩어리로 본다. 트리거의 화이트리스트와
        /// 마이그레이션 UPDATE 두 곳에 같은 표식이 붙으므로 결과는 둘이다.
        /// </summary>
        private static IReadOnlyList<string> TrackedTypeLists()
        {
            var script = StateTracker.ReadInstallScript();
            var results = new List<string>();

            foreach (Match marker in Regex.Matches(script, "DBVC_TRACKED_TYPES"))
            {
                var rest = script.Substring(marker.Index);
                var end = rest.IndexOf(';');
                results.Add(end > 0 ? rest.Substring(0, end) : rest);
            }

            return results;
        }

        private static string[] ParseTypes(string block)
            => Regex.Matches(block, @"N'([^']+)'").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

        [Test]
        public void InstallScript_TracksExactlyTheObjectTypesTheConventionKnows_PlusIndex()
        {
            var expected = ObjectPathConvention.DdlEventObjectTypes.Concat(new[] { "INDEX" }).ToArray();

            var lists = TrackedTypeLists();
            Assert.That(lists, Is.Not.Empty, "설치 스크립트에서 DBVC_TRACKED_TYPES 표식을 찾지 못했습니다");

            foreach (var block in lists)
            {
                Assert.That(ParseTypes(block), Is.EquivalentTo(expected),
                    "설치 스크립트의 타입 목록이 ObjectPathConvention과 다릅니다");
            }
        }
    }
}
```

`InternalsVisibleTo("DBVC.Core.Tests")`는 `StateTracker.cs`에 이미 있으므로 `DdlEventObjectTypes`가 테스트에서 보인다.

- [ ] **Step 6: 두 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~InstallScriptSyncTests|FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: PASS

- [ ] **Step 7: 전체 테스트**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS

- [ ] **Step 8: 커밋**

```bash
git add src/DBVC.Core/ObjectPathConvention.cs src/DBVC.Database/InstallTrigger.sql tests/DBVC.Core.Tests/InstallScriptSyncTests.cs tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs
git commit -m "fix(db): 스크립팅할 수 없는 DDL 이벤트를 기록하지 않는다"
```

---

### Task 3: 인덱스 이벤트에 부모를 남긴다

**Files:**
- Modify: `src/DBVC.Database/InstallTrigger.sql`
- Modify: `src/DBVC.Core/Models/ChangeRecord.cs`
- Modify: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`

**Interfaces:**
- Produces: `ChangeLogRow.TargetObjectName` (`string?`), `ChangeLogRow.TargetObjectType` (`string?`) — Task 5의 정규화가 읽는다. `DBVC_ChangeLog`에 같은 이름의 컬럼.

- [ ] **Step 1: 인덱스 이벤트가 부모를 남긴다는 테스트를 쓴다**

`DdlTriggerIntegrationTests.cs`에 더한다:

```csharp
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
        public void InstallScript_IsIdempotent_WhenRunTwice()
        {
            // 재설치는 업데이트 경로이기도 하다. 두 번째 실행이 실패하면 구버전 사용자가 올라갈 길이 없다.
            var tracker = new StateTracker(NewConfig());
            Assert.DoesNotThrow(() => tracker.InitializeDatabase(SqlServerTestDatabase.ServerName, _db!.Name));
            Assert.DoesNotThrow(() => tracker.InitializeDatabase(SqlServerTestDatabase.ServerName, _db!.Name));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~Trigger_RecordsTheParentTable"`
Expected: FAIL — `Invalid column name 'TargetObjectName'`

- [ ] **Step 3: 테이블에 컬럼을 더한다**

`InstallTrigger.sql`의 `CREATE TABLE [dbo].[DBVC_ChangeLog]` 안, `[TSQLCommand]` 다음 줄에 넣는다:

```sql
        [TargetObjectName] NVARCHAR(256) NULL,
        [TargetObjectType] NVARCHAR(100) NULL,
```

그리고 기존 구버전 보정 블록들(`SchemaName`, `IsProcessed`) 옆에 같은 형태로 더한다:

```sql
-- v1(Target 컬럼 이전)에 설치된 테이블 보정. 인덱스 이벤트가 부모 테이블을 가리키는 유일한 근거다.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'TargetObjectName')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [TargetObjectName] NVARCHAR(256) NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'TargetObjectType')
BEGIN
    ALTER TABLE [dbo].[DBVC_ChangeLog] ADD [TargetObjectType] NVARCHAR(100) NULL;
END
GO
```

- [ ] **Step 4: 트리거가 값을 넣게 한다**

INSERT의 컬럼 목록에 `[TargetObjectName], [TargetObjectType]`를 더하고, VALUES에 대응하는 두 줄을 더한다:

```sql
        @EventData.value('(/EVENT_INSTANCE/TargetObjectName)[1]', 'NVARCHAR(256)'),
        @EventData.value('(/EVENT_INSTANCE/TargetObjectType)[1]', 'NVARCHAR(100)'),
```

(`IsProcessed`의 `0` 앞에 오도록 순서를 맞춘다. 트리거는 사실만 남기고 해석하지 않는다 — 인덱스 이벤트의 `ObjectName`은 그대로 인덱스 이름이다.)

- [ ] **Step 5: 모델에 두 속성을 더한다**

`src/DBVC.Core/Models/ChangeRecord.cs`의 `ChangeLogRow`에:

```csharp
        /// <summary>인덱스처럼 다른 객체에 딸린 이벤트의 부모. 없으면 null이다.</summary>
        public string? TargetObjectName { get; set; }

        public string? TargetObjectType { get; set; }
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: PASS (4개)

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Database/InstallTrigger.sql src/DBVC.Core/Models/ChangeRecord.cs tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs
git commit -m "feat(db): 인덱스 이벤트에 부모 객체를 함께 기록한다"
```

---

### Task 4: 스키마 버전과 버전 조회

**Files:**
- Modify: `src/DBVC.Database/InstallTrigger.sql`
- Modify: `src/DBVC.Core/StateTracker.cs`
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:339`
- Modify: `tests/DBVC.Core.Tests/StateTrackerTests.cs`, `tests/DBVC.Core.Tests/InstallScriptSyncTests.cs`, `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`, `tests/DBVC.Vsix.Tests/Services/BackgroundSchedulerWiringTests.cs`

**Interfaces:**
- Produces:
  - `StateTracker.RequiredSchemaVersion` — `public const int` = `2`
  - `IStateTracker.GetInstalledVersion(string serverName, string databaseName)` → `int` (0=미설치, 1=구버전, 2=현재). `IsInitialized`를 **대체한다**.
  - `StateTracker.InstalledVersionQuery` — `internal const string`

- [ ] **Step 1: 버전 조회 테스트를 쓴다**

`tests/DBVC.Core.Tests/StateTrackerTests.cs`의 기존 `IsInitialized*` 테스트 세 개(`IsInitialized_ReturnsFalse_WhenTheServerCannotBeReached`, `IsInitialized_ReturnsFalse_WhenServerOrDatabaseIsMissing`, `IsInitializedQuery_ChecksBothTheChangeLogTableAndTheDdlTrigger`)를 아래로 바꾼다:

```csharp
        [Test]
        public void GetInstalledVersion_ReturnsZero_WhenTheServerCannotBeReached()
        {
            Assert.That(NewTracker().GetInstalledVersion("no_such_server_hostname", "no_such_db"), Is.Zero);
        }

        [Test]
        public void GetInstalledVersion_ReturnsZero_WhenServerOrDatabaseIsMissing()
        {
            var tracker = NewTracker();
            Assert.That(tracker.GetInstalledVersion("", "db"), Is.Zero);
            Assert.That(tracker.GetInstalledVersion("server", ""), Is.Zero);
        }

        [Test]
        public void InstalledVersionQuery_ChecksTheChangeLogTableTheTriggerAndTheVersionProperty()
        {
            // 셋 중 하나라도 빠지면 구버전을 최신으로 읽거나, 설치된 것을 미설치로 읽는다.
            var query = StateTracker.InstalledVersionQuery;

            Assert.Multiple(() =>
            {
                Assert.That(query, Does.Contain("DBVC_ChangeLog"));
                Assert.That(query, Does.Contain("trg_DBVC_DDL_Tracker"));
                Assert.That(query, Does.Contain("DBVC_SchemaVersion"));
            });
        }

        [Test]
        public void RequiredSchemaVersion_IsTwo()
        {
            // 설치 스크립트가 심는 값과 같아야 한다. 어긋나면 모든 사용자에게 업데이트 배너가 계속 뜨거나
            // 구버전이 최신으로 읽힌다. 스크립트 쪽 값은 InstallScriptSyncTests가 대조한다.
            Assert.That(StateTracker.RequiredSchemaVersion, Is.EqualTo(2));
        }
```

`InstallScriptSyncTests.cs`에 더한다:

```csharp
        [Test]
        public void InstallScript_StampsTheVersionCoreRequires()
        {
            var script = StateTracker.ReadInstallScript();
            var match = Regex.Match(script, @"@name\s*=\s*N'DBVC_SchemaVersion'\s*,\s*@value\s*=\s*N'(\d+)'");

            Assert.That(match.Success, Is.True, "설치 스크립트에서 DBVC_SchemaVersion 값을 찾지 못했습니다");
            Assert.That(int.Parse(match.Groups[1].Value), Is.EqualTo(StateTracker.RequiredSchemaVersion));
        }
```

`DdlTriggerIntegrationTests.cs`에 더한다:

```csharp
        [Test]
        public void GetInstalledVersion_ReturnsTheRequiredVersion_AfterInstall()
        {
            var version = new StateTracker(NewConfig())
                .GetInstalledVersion(SqlServerTestDatabase.ServerName, _db!.Name);

            Assert.That(version, Is.EqualTo(StateTracker.RequiredSchemaVersion));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~StateTrackerTests"`
Expected: FAIL — 컴파일 오류(`GetInstalledVersion`, `InstalledVersionQuery`, `RequiredSchemaVersion` 없음)

- [ ] **Step 3: 스크립트에 버전 표식을 심는다**

`InstallTrigger.sql` 맨 끝(트리거 생성 다음)에 넣는다:

```sql
-- 스키마 버전. Core(StateTracker.RequiredSchemaVersion)가 이 값을 보고 구버전 설치를 알아챈다.
-- 확장 속성을 쓰는 이유는 객체가 늘지 않고, 이 DDL 자체가 트리거의 DBVC_ChangeLog 예외에 걸려
-- 로그를 더럽히지 않기 때문이다.
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE class = 1 AND major_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]')
                 AND minor_id = 0 AND name = N'DBVC_SchemaVersion')
BEGIN
    EXEC sp_addextendedproperty @name = N'DBVC_SchemaVersion', @value = N'2',
         @level0type = N'SCHEMA', @level0name = N'dbo',
         @level1type = N'TABLE',  @level1name = N'DBVC_ChangeLog';
END
ELSE
BEGIN
    EXEC sp_updateextendedproperty @name = N'DBVC_SchemaVersion', @value = N'2',
         @level0type = N'SCHEMA', @level0name = N'dbo',
         @level1type = N'TABLE',  @level1name = N'DBVC_ChangeLog';
END
GO
```

- [ ] **Step 4: StateTracker에 버전 조회를 넣는다**

`src/DBVC.Core/StateTracker.cs`에서 `IsInitializedQuery`(24행)와 `IsInitialized`(83행)를 아래로 **대체한다**:

```csharp
        /// <summary>설치 스크립트가 심는 스키마 버전. 이 값보다 낮으면 도구 창이 업데이트를 안내한다.</summary>
        public const int RequiredSchemaVersion = 2;

        /// <summary>
        /// 설치 상태를 한 번의 왕복으로 판정한다.
        /// 0 = 미설치, 1 = 버전 표식이 없던 시절의 설치, 그 외 = 심어진 값.
        /// </summary>
        internal const string InstalledVersionQuery = @"
SELECT CASE
    WHEN NOT EXISTS (SELECT 1 FROM sys.objects
                     WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND type = N'U')
      OR NOT EXISTS (SELECT 1 FROM sys.triggers
                     WHERE parent_class = 0 AND name = N'trg_DBVC_DDL_Tracker')
    THEN 0
    ELSE ISNULL((SELECT TRY_CAST(CAST(value AS NVARCHAR(50)) AS int)
                 FROM sys.extended_properties
                 WHERE class = 1 AND major_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]')
                   AND minor_id = 0 AND name = N'DBVC_SchemaVersion'), 1)
END";

        /// <summary>
        /// 설치된 스키마 버전을 반환한다. 접속 실패는 0으로 알린다 — 사유는 <see cref="TestConnection"/>이
        /// 따로 만들며, 여기서 구분하면 호출자가 같은 배너를 두 곳에서 채우게 된다.
        /// </summary>
        public int GetInstalledVersion(string serverName, string databaseName)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName)) return 0;
            try
            {
                using var conn = new SqlConnection(_connectionFactory.Build(serverName, databaseName));
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = InstalledVersionQuery;
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StateTracker.GetInstalledVersion failed: {ex.Message}");
                return 0;
            }
        }
```

165행 부근 `TestConnection`의 XML 주석에서 `<see cref="IsInitialized"/>`를 `<see cref="GetInstalledVersion"/>`으로 고친다.

- [ ] **Step 5: 인터페이스를 바꾼다**

`src/DBVC.Core/Abstractions.cs:39`:

```csharp
        /// <summary>설치된 스키마 버전. 0이면 미설치다.</summary>
        int GetInstalledVersion(string serverName, string databaseName);
```

- [ ] **Step 6: ViewModel을 컴파일되게 고친다**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:339`:

```csharp
                probe.IsInitialized = _stateTracker.GetInstalledVersion(server, database) > 0;
```

(구버전 안내는 Task 8에서 붙인다. 여기서는 기존 동작을 그대로 유지한다.)

- [ ] **Step 7: 테스트 목을 새 API로 바꾼다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`와 `tests/DBVC.Vsix.Tests/Services/BackgroundSchedulerWiringTests.cs`에서 아래 형태를 모두 바꾼다(16 + 1곳):

```csharp
// 전
_stateTracker.Setup(s => s.IsInitialized(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
// 후
_stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>())).Returns(StateTracker.RequiredSchemaVersion);
```

`Returns(false)`로 "초기화되지 않음"을 만들던 자리는 `Returns(0)`으로 바꾼다.

- [ ] **Step 8: 전체 테스트**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS (양쪽)

- [ ] **Step 9: 커밋**

```bash
git add src/DBVC.Database/InstallTrigger.sql src/DBVC.Core/StateTracker.cs src/DBVC.Core/Abstractions.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/
git commit -m "feat(core): 설치된 스키마 버전을 읽는다"
```

---

### Task 5: 인덱스 이벤트를 부모 테이블로 정규화한다

**Files:**
- Modify: `src/DBVC.Core/StateTracker.cs`
- Modify: `tests/DBVC.Core.Tests/StateTrackerTests.cs`

**Interfaces:**
- Consumes: `ChangeLogRow.TargetObjectName`/`TargetObjectType` (Task 3)
- Produces: `StateTracker.NormalizeRow(ChangeLogRow row)` → `ChangeLogRow` (`internal static`)

- [ ] **Step 1: 정규화 테스트를 쓴다 (삭제 함정부터)**

`StateTrackerTests.cs`에 더한다. `Row` 헬퍼는 Target을 받지 않으므로 테스트 안에서 객체를 직접 만든다:

```csharp
        // ---------- 인덱스 이벤트 정규화 ----------

        private static ChangeLogRow IndexRow(string eventType, string indexName, string? targetName = "Users")
            => new ChangeLogRow
            {
                Id = 10,
                SchemaName = "dbo",
                ObjectName = indexName,
                ObjectType = "INDEX",
                EventType = eventType,
                TargetObjectName = targetName,
                TargetObjectType = targetName == null ? null : "TABLE"
            };

        [Test]
        public void NormalizeRow_TreatsADroppedIndexAsAModifiedParentTable_NotADeletedObject()
        {
            // 이름만 바꾸고 이벤트를 그대로 두면 상태가 Deleted가 되고, WorkingTreeCleaner가
            // 그것을 보고 테이블의 .sql을 지운다 - 인덱스를 지웠을 뿐인데 저장소에서 테이블이 사라진다.
            var normalized = StateTracker.NormalizeRow(IndexRow("DROP_INDEX", "IX_Users_Name"));

            Assert.Multiple(() =>
            {
                Assert.That(normalized.ObjectName, Is.EqualTo("Users"));
                Assert.That(normalized.ObjectType, Is.EqualTo("TABLE"));
                Assert.That(StateTracker.MapEventTypeToState(normalized.EventType), Is.EqualTo("Modified"));
            });
        }

        [Test]
        [TestCase("CREATE_INDEX")]
        [TestCase("ALTER_INDEX")]
        public void NormalizeRow_PointsIndexEventsAtTheParentTable(string eventType)
        {
            var normalized = StateTracker.NormalizeRow(IndexRow(eventType, "IX_Users_Name"));

            Assert.That(normalized.ObjectName, Is.EqualTo("Users"));
            Assert.That(normalized.ObjectType, Is.EqualTo("TABLE"));
        }

        [Test]
        public void NormalizeRow_LeavesTheRowAlone_WhenTheParentIsUnknown()
        {
            // v1이 남긴 행이다. 부모를 지어낼 수 없으므로 손대지 않는다.
            var normalized = StateTracker.NormalizeRow(IndexRow("CREATE_INDEX", "IX_Users_Name", targetName: null));

            Assert.That(normalized.ObjectName, Is.EqualTo("IX_Users_Name"));
        }

        [Test]
        public void NormalizeRow_LeavesNonIndexRowsAlone()
        {
            var row = Row(1, "dbo", "Users", "TABLE", "ALTER_TABLE");

            var normalized = StateTracker.NormalizeRow(row);

            Assert.That(normalized.ObjectName, Is.EqualTo("Users"));
            Assert.That(normalized.EventType, Is.EqualTo("ALTER_TABLE"));
        }

        [Test]
        public void ToQualifiedNames_YieldsTheParentTable_ForNormalizedIndexRows()
        {
            // 추출 대상 목록에도 부모가 나와야 새로고침이 테이블을 다시 스크립팅한다.
            var names = StateTracker.ToQualifiedNames(new[] { StateTracker.NormalizeRow(IndexRow("CREATE_INDEX", "IX_Users_Name")) });

            Assert.That(names, Is.EqualTo(new[] { "dbo.Users" }));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~NormalizeRow"`
Expected: FAIL — 컴파일 오류(`NormalizeRow` 없음)

- [ ] **Step 3: 정규화를 구현한다**

`StateTracker.cs`의 `ToQualifiedNames` 위에 넣는다:

```csharp
        /// <summary>
        /// 인덱스 이벤트를 부모 객체의 변경으로 바꾼다. 로그를 읽는 입구에서 한 번만 부른다 —
        /// 추출 대상(<see cref="GetChangedObjectNames"/>)과 화면 목록(<see cref="BuildChangeSet"/>)이
        /// 각자 해석하면 추출은 테이블을 뽑았는데 목록은 인덱스를 보여주는 식으로 갈라진다.
        ///
        /// 이벤트 타입도 함께 옮기는 것이 핵심이다. DROP_INDEX를 그대로 두면 상태가 Deleted가 되고
        /// WorkingTreeCleaner가 테이블의 .sql을 지운다 - 인덱스 하나를 지웠을 뿐인데.
        /// 인덱스 변경은 부모 테이블의 수정이지 삭제가 아니다.
        ///
        /// 부모를 모르면(v1이 남긴 행) 손대지 않는다. 지어낼 근거가 없다.
        /// </summary>
        internal static ChangeLogRow NormalizeRow(ChangeLogRow row)
        {
            if (row == null) return row!;
            if (!string.Equals(row.ObjectType?.Trim(), "INDEX", StringComparison.OrdinalIgnoreCase)) return row;
            if (string.IsNullOrWhiteSpace(row.TargetObjectName)) return row;

            return new ChangeLogRow
            {
                Id = row.Id,
                SchemaName = row.SchemaName,
                ObjectName = row.TargetObjectName!.Trim(),
                ObjectType = string.IsNullOrWhiteSpace(row.TargetObjectType) ? "TABLE" : row.TargetObjectType!.Trim(),
                EventType = "ALTER_TABLE",
                TargetObjectName = row.TargetObjectName,
                TargetObjectType = row.TargetObjectType
            };
        }
```

- [ ] **Step 4: 읽는 입구에서 부른다**

`StateTracker.ReadPendingRows`의 쿼리와 매핑을 바꾼다. `PendingChangesQuery`에 컬럼 둘을 더한다:

```csharp
        internal const string PendingChangesQuery = @"
SELECT Id, SchemaName, ObjectName, ObjectType, EventType, TargetObjectName, TargetObjectType
FROM dbo.DBVC_ChangeLog
WHERE IsProcessed = 0
ORDER BY PostTime DESC, Id DESC";
```

`ReadPendingRows`의 `rows.Add(...)`를 바꾼다:

```csharp
                rows.Add(NormalizeRow(new ChangeLogRow
                {
                    Id = reader.GetInt32(0),
                    SchemaName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ObjectName = reader.GetString(2),
                    ObjectType = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    EventType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    TargetObjectName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    TargetObjectType = reader.IsDBNull(6) ? null : reader.GetString(6)
                }));
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS (전체)

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/StateTrackerTests.cs
git commit -m "feat(core): 인덱스 변경을 부모 테이블의 수정으로 읽는다"
```

---

### Task 6: 커밋이 인덱스 행까지 닫게 한다

**Files:**
- Modify: `src/DBVC.Core/StateTracker.cs`
- Modify: `tests/DBVC.Core.Tests/StateTrackerTests.cs`
- Modify: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`

**Interfaces:**
- Produces: `StateTracker.MarkProcessedCommand` — `private const`에서 `internal const`로 공개 범위만 넓힌다.

- [ ] **Step 1: 테스트를 쓴다**

`StateTrackerTests.cs`에 더한다:

```csharp
        [Test]
        public void MarkProcessedCommand_ClosesRowsThatPointAtTheObjectAsTheirParent()
        {
            // 정규화 뒤 레코드의 이름은 테이블인데 로그의 행은 인덱스 이름이다. ObjectName만 보면
            // 인덱스 행이 닫히지 않아 커밋해도 다음 새로고침에 그대로 다시 올라온다.
            var command = StateTracker.MarkProcessedCommand;

            Assert.Multiple(() =>
            {
                Assert.That(command, Does.Contain("TargetObjectName = @objectName"));
                Assert.That(command, Does.Contain("Id <= @lastLogId"), "새로고침 이후의 이벤트는 건드리지 않아야 한다");
            });
        }
```

`DdlTriggerIntegrationTests.cs`에 실제 동작을 검증하는 테스트를 더한다:

```csharp
        [Test]
        public void MarkProcessed_ClosesTheIndexRow_WhenTheParentTableIsCommitted()
        {
            _db!.Execute("CREATE TABLE dbo.MarkedTable (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL)");
            _db.Execute("CREATE NONCLUSTERED INDEX IX_MarkedTable_Name ON dbo.MarkedTable (Name)");

            var tracker = new StateTracker(NewConfig());
            var maxId = Convert.ToInt64(_db.QueryScalar(
                "SELECT MAX(Id) FROM dbo.DBVC_ChangeLog WHERE ObjectName IN (N'MarkedTable', N'IX_MarkedTable_Name')"));

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
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MarkProcessed"`
Expected: FAIL — 단위 테스트는 컴파일 오류(`MarkProcessedCommand`가 private), 통합 테스트는 `Expected: 0 But was: 1`

- [ ] **Step 3: 조건을 넓힌다**

`StateTracker.cs`의 `MarkProcessedCommand`를 바꾼다:

```csharp
        /// <summary>
        /// 커밋된 객체의 로그 행을 닫는다. TargetObjectName까지 보는 이유는 정규화 때문이다 -
        /// 레코드의 이름은 부모 테이블인데 인덱스 행의 ObjectName은 인덱스 이름이라,
        /// ObjectName만 보면 그 행이 영원히 열린 채로 남아 매번 다시 올라온다.
        /// </summary>
        internal const string MarkProcessedCommand = @"
UPDATE dbo.DBVC_ChangeLog
SET IsProcessed = 1
WHERE IsProcessed = 0 AND Id <= @lastLogId
  AND (ObjectName = @objectName OR TargetObjectName = @objectName)
  AND (ISNULL(SchemaName, N'dbo') = @schemaName)";
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS (전체)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/
git commit -m "fix(core): 테이블을 커밋하면 인덱스 로그도 함께 닫는다"
```

---

### Task 7: 구버전이 남긴 커밋 불가 행을 닫는다

**Files:**
- Modify: `src/DBVC.Database/InstallTrigger.sql`
- Modify: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 2의 `DBVC_TRACKED_TYPES` 표식 규약(같은 표식을 이 UPDATE에도 붙인다 — 동기화 테스트가 두 곳을 모두 검사한다)

- [ ] **Step 1: v1 → v2 업그레이드 테스트를 쓴다**

`DdlTriggerIntegrationTests.cs`에 더한다. 이 테스트만 별도 데이터베이스를 쓴다 — 공용 DB에 v1 상태를 만들면 다른 테스트의 전제가 무너진다.

```csharp
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

            Assert.Multiple(() =>
            {
                Assert.That(Convert.ToInt32(stillOpen), Is.EqualTo(1), "커밋 불가 행 둘이 닫혀야 한다");
                Assert.That(Convert.ToInt32(realOpen), Is.EqualTo(1), "커밋할 수 있는 변경까지 닫으면 안 된다");
            });
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~InstallScript_ClosesRowsThatCanNeverBeCommitted"`
Expected: FAIL — `Expected: 1 But was: 3`

- [ ] **Step 3: 마이그레이션 UPDATE를 넣는다**

`InstallTrigger.sql` 맨 끝(버전 표식 다음)에 넣는다. 컬럼 추가 블록보다 뒤여야 `TargetObjectName`을 참조할 수 있다.

```sql
-- v1이 남긴 커밋 불가 행을 닫는다. (a) 화이트리스트 밖 타입은 .sql이 만들어질 수 없고,
-- (b) 부모를 모르는 인덱스 행은 정규화할 수 없다. 그대로 두면 목록에 영원히 남는다.
-- v2 트리거는 이런 행을 애초에 만들지 않으므로 이 정리는 옛 행에만 닿고, 여러 번 실행해도 결과가 같다.
-- DBVC_TRACKED_TYPES: 위 트리거의 목록과 같아야 한다. InstallScriptSyncTests가 두 곳을 함께 검사한다.
UPDATE [dbo].[DBVC_ChangeLog]
SET [IsProcessed] = 1
WHERE [IsProcessed] = 0
  AND ([ObjectType] NOT IN (N'TABLE', N'VIEW', N'PROCEDURE', N'SQL_STORED_PROCEDURE',
        N'FUNCTION', N'SQL_SCALAR_FUNCTION', N'SQL_TABLE_VALUED_FUNCTION',
        N'SQL_INLINE_TABLE_VALUED_FUNCTION', N'TRIGGER', N'SQL_TRIGGER', N'TYPE',
        N'TABLE_TYPE', N'SEQUENCE OBJECT', N'SEQUENCE_OBJECT', N'SEQUENCE', N'SYNONYM',
        N'INDEX')
       OR ([ObjectType] = N'INDEX' AND [TargetObjectName] IS NULL));
GO
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS (전체). `InstallScript_TracksExactlyTheObjectTypesTheConventionKnows_PlusIndex`가 이제 두 목록을 검사한다.

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Database/InstallTrigger.sql tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs
git commit -m "fix(db): 구버전이 남긴 커밋 불가 로그를 닫는다"
```

---

### Task 8: 구버전을 알리고 갈아 끼운다

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`ProbeContext` 328-345, `ApplyContextProbe` 347-370, `Setup` 739-761)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IStateTracker.GetInstalledVersion` (Task 4), `StateTracker.RequiredSchemaVersion`
- Produces: `ViewChangesViewModel.IsTrackerOutdated` (`bool`), `ViewChangesViewModel.UpdateTrackerCommand` (`ICommand`)

- [ ] **Step 1: 테스트를 쓴다**

`ViewChangesViewModelTests.cs`에 더한다:

```csharp
        // ---------- 추적기 버전 ----------

        [Test]
        public void IsTrackerOutdated_IsTrue_WhenTheInstalledVersionIsBehind()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(1);

            var vm = NewConnectedViewModel();

            Assert.Multiple(() =>
            {
                Assert.That(vm.IsTrackerOutdated, Is.True);
                Assert.That(vm.IsInitialized, Is.True, "구버전도 설치된 것이다 - 초기화 오버레이를 다시 띄우면 안 된다");
            });
        }

        [Test]
        public void IsTrackerOutdated_IsFalse_WhenTheTrackerIsCurrent()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(StateTracker.RequiredSchemaVersion);

            Assert.That(NewConnectedViewModel().IsTrackerOutdated, Is.False);
        }

        [Test]
        public void IsTrackerOutdated_IsFalse_WhenNothingIsInstalled()
        {
            // 미설치는 초기화 오버레이가 맡는다. 두 안내가 함께 뜨면 무엇을 눌러야 하는지 흐려진다.
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(0);

            Assert.That(NewConnectedViewModel().IsTrackerOutdated, Is.False);
        }

        [Test]
        public void UpdateTracker_ReinstallsTheScript_AndTellsTheUserToReExtract()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(1);
            var vm = NewConnectedViewModel();

            vm.UpdateTrackerCommand.Execute(null);

            _stateTracker.Verify(s => s.InitializeDatabase(Server, Database), Times.Once);
            Assert.That(_notifier.Infos.Any(m => m.Contains("전체 다시 추출")), Is.True,
                "과거 인덱스 변경이 정리되면서 사라지므로 다시 추출하라고 알려야 한다");
        }

        [Test]
        public void UpdateTracker_ShowsTheReason_WhenInstallFails()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(1);
            _stateTracker.Setup(s => s.InitializeDatabase(Server, Database))
                .Throws(new InvalidOperationException("권한이 없습니다"));
            var vm = NewConnectedViewModel();

            vm.UpdateTrackerCommand.Execute(null);

            Assert.That(_notifier.Errors.Any(m => m.Contains("권한이 없습니다")), Is.True);
        }

        [Test]
        public void Setup_RunsThroughTheScheduler()
        {
            // 설치는 응답 없는 서버에서 수십 초까지 걸린다. UI 스레드에 남으면 그동안 SSMS가 멈춘다.
            var scheduler = new CountingScheduler();
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(0);
            var vm = new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _folderDialog, _credentials.Object, _ssms.Object, scheduler);
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            vm.ConnectCommand.Execute(null);
            var before = scheduler.RunCount;

            vm.SetupCommand.Execute(null);

            Assert.That(scheduler.RunCount, Is.GreaterThan(before));
        }

        /// <summary>넘겨받은 작업을 인라인으로 실행하되 횟수를 센다.</summary>
        private sealed class CountingScheduler : IBackgroundScheduler
        {
            public int RunCount { get; private set; }

            public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
            {
                RunCount++;
                T value;
                try { value = work(); }
                catch (Exception ex) { onFailed(ex); return; }
                onSucceeded(value);
            }

            public void Post(Action action) => action();
        }
```

`RecordingNotifier`(같은 파일 2125행)는 이미 `Infos`/`Errors`를 `List<string>`(메시지만)으로 노출하므로 손댈 것이 없다. `CountingScheduler`는 이 파일에 새로 넣는다 — `BackgroundSchedulerWiringTests`의 `RecordingScheduler`는 그쪽 파일의 `private` 중첩 클래스라 여기서 보이지 않는다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~TrackerOutdated|FullyQualifiedName~UpdateTracker|FullyQualifiedName~Setup_RunsThroughTheScheduler"`
Expected: FAIL — 컴파일 오류(`IsTrackerOutdated`, `UpdateTrackerCommand` 없음)

- [ ] **Step 3: ViewModel에 상태와 명령을 더한다**

`ContextProbe`에 버전을 담는다(`IsInitialized` 필드를 버전으로 바꾼다):

```csharp
        private sealed class ContextProbe
        {
            public string? ConnectionError { get; set; }
            public bool IsMapped { get; set; }
            public int InstalledVersion { get; set; }
        }
```

`ProbeContext`:

```csharp
            if (probe.ConnectionError == null)
            {
                probe.InstalledVersion = _stateTracker.GetInstalledVersion(server, database);
            }
```

`ApplyContextProbe`에서 `IsInitialized = probe.IsInitialized;`를 바꾼다:

```csharp
            IsInitialized = probe.InstalledVersion > 0;

            // 미설치는 초기화 오버레이가 맡는다. 두 안내를 함께 띄우면 무엇을 눌러야 하는지 흐려진다.
            IsTrackerOutdated = probe.InstalledVersion > 0
                && probe.InstalledVersion < StateTracker.RequiredSchemaVersion;
```

`ConnectionError` 갈래에서는 `IsTrackerOutdated = false;`도 함께 내린다. `InvalidateActiveContext()`에도 같은 줄을 더한다.

속성과 명령을 더한다:

```csharp
        private bool _isTrackerOutdated;

        /// <summary>
        /// 설치된 추적기가 지금 Core가 요구하는 버전보다 낮은지. 참이면 인덱스 변경이 감지되지 않는다.
        /// </summary>
        public bool IsTrackerOutdated
        {
            get => _isTrackerOutdated;
            private set
            {
                if (_isTrackerOutdated == value) return;
                _isTrackerOutdated = value;
                OnPropertyChanged();
                RaiseActionCanExecuteChanged();
            }
        }

        /// <summary>구버전 추적기를 현재 버전으로 다시 설치한다.</summary>
        public ICommand UpdateTrackerCommand { get; }
```

생성자에 더한다(다른 명령들 옆):

```csharp
            UpdateTrackerCommand = new RelayCommand(UpdateTracker, () => IsTrackerOutdated && !IsBusy);
```

`RaiseActionCanExecuteChanged()`에 한 줄 더한다:

```csharp
            (UpdateTrackerCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

- [ ] **Step 4: 설치를 백그라운드로 옮기고 두 진입점을 합친다**

`Setup()`(739행)을 아래로 바꾼다:

```csharp
        private void Setup()
        {
            if (!HasContext)
            {
                _notifier.ShowError("DBVC", "먼저 개체 탐색기에서 대상 데이터베이스를 선택하세요.");
                return;
            }

            InstallSchema(isUpdate: false);
        }

        /// <summary>구버전 추적기를 다시 설치한다. 스크립트가 멱등이라 초기화와 같은 경로다.</summary>
        private void UpdateTracker()
        {
            if (!HasContext || !IsTrackerOutdated) return;

            InstallSchema(isUpdate: true);
        }

        /// <summary>
        /// 설치 스크립트를 실행한다. DDL 여러 배치를 도는 일이라 응답 없는 서버에서는 수십 초까지
        /// 걸린다 - UI 스레드에 남기면 그동안 SSMS 전체가 멈춘다.
        /// </summary>
        private void InstallSchema(bool isUpdate)
        {
            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            ProgressText = isUpdate ? "변경 추적기를 업데이트하는 중..." : "DBVC를 초기화하는 중...";

            _scheduler.Run<object?>(
                () => { _stateTracker.InitializeDatabase(server, database); return null; },
                _ =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    IsInitialized = true;
                    IsTrackerOutdated = false;

                    if (isUpdate)
                    {
                        // 부모를 모르는 옛 인덱스 로그가 이때 닫힌다. 그 변경은 저장소에 반영된 적이
                        // 없을 수 있으므로, 되찾는 유일한 경로를 알려 준다.
                        _notifier.ShowInfo(
                            "DBVC",
                            "변경 추적기를 업데이트했습니다." + Environment.NewLine +
                            "그동안의 인덱스 변경이 저장소에 없을 수 있으니 전체 다시 추출을 한 번 눌러 주세요.");
                        Refresh();
                        return;
                    }

                    // 방금 트리거를 설치했다. 그 이전의 변경은 DDL 로그에 없으므로 전체를 추출해야
                    // 저장소가 DB의 현재 상태를 담는다.
                    RefreshAll();
                },
                ex =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    // 설치 실패(권한 부족 등)를 성공으로 위장해서는 안 된다.
                    _notifier.ShowError(isUpdate ? "DBVC 추적기 업데이트 실패" : "DBVC 초기화 실패", ex.Message);
                });
        }
```

- [ ] **Step 5: 화면에 한 줄을 더한다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml`의 Row 0 `StackPanel` 안, `SsmsHintMessage` `TextBlock` **다음**에 넣는다. 새 `RowDefinition`을 만들지 않는 이유는 Grid의 행 번호가 밀리면 아래 오버레이까지 함께 고쳐야 하기 때문이다.

```xml
            <!--
                구버전 추적기 안내. 위의 경고 배너(WarningMessage)에 얹지 않는다 - 그쪽은 접속 실패·
                매핑 없음으로 이미 쓰이고 있어 겹치면 한쪽이 다른 쪽을 지운다.
            -->
            <Border Background="#FFF4CE" BorderBrush="#E0C77A" BorderThickness="1"
                    Padding="8,5" Margin="5,0,5,4"
                    Visibility="{Binding IsTrackerOutdated, Converter={StaticResource BoolToVis}}">
                <DockPanel LastChildFill="True">
                    <Button DockPanel.Dock="Right" Content="추적기 업데이트" Width="110" Margin="8,0,0,0"
                            Command="{Binding UpdateTrackerCommand}"
                            ToolTip="변경 추적 트리거를 현재 버전으로 다시 설치합니다. 데이터베이스 스키마는 바뀌지 않습니다."/>
                    <TextBlock Text="변경 추적기가 구버전입니다. 인덱스 변경이 저장소에 반영되지 않습니다."
                               Foreground="#6B5A00" TextWrapping="Wrap" FontWeight="SemiBold"
                               VerticalAlignment="Center"/>
                </DockPanel>
            </Border>
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS (전체)

- [ ] **Step 7: 솔루션 전체를 빌드한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공. XAML은 컴파일 단계에서만 검증되므로 여기서 오타가 드러난다.

- [ ] **Step 8: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 구버전 추적기를 알리고 업데이트 버튼을 준다"
```

---

### Task 9: 문서와 버전

**Files:**
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest:4`
- Modify: `README.md`
- Modify: `docs/setup-checklist.md`

- [ ] **Step 1: 확장 버전을 올린다**

`source.extension.vsixmanifest`의 `Version="0.2.5"`를 `Version="0.2.6"`으로 바꾼다.

- [ ] **Step 2: README를 고친다**

"주요 기능"의 변경 감지 항목에 인덱스를 명시하고, "동작 방식"에 아래 문단을 더한다:

```markdown
- **변경 추적기 업데이트:** 0.2.6에서 변경 추적 트리거가 바뀌었습니다. 그 이전에 초기화한
  데이터베이스에 접속하면 창 위쪽에 **"변경 추적기가 구버전입니다"** 안내와 **추적기 업데이트**
  버튼이 뜹니다. 누르면 트리거만 다시 설치되며 데이터베이스 스키마는 바뀌지 않습니다.
  업데이트 뒤에는 **전체 다시 추출** 을 한 번 눌러 주세요 — 그동안의 인덱스 변경이 저장소에
  반영되지 않았을 수 있습니다.
- **인덱스 변경:** `CREATE INDEX` · `DROP INDEX` 는 부모 테이블의 수정으로 기록되어, 새로고침이
  그 테이블을 다시 추출합니다. 인덱스는 테이블 `.sql` 안에 담기므로 따로 파일이 생기지 않습니다.
- **DBVC를 쓰지 않는 사용자:** 변경 추적 트리거는 `dbo` 권한으로 로그를 남기므로, 데이터베이스에
  DDL 권한만 있고 DBVC를 쓰지 않는 팀원의 작업을 막지 않습니다.
```

- [ ] **Step 3: setup-checklist를 고친다**

5단계(데이터베이스 초기화)에 요구사항 한 줄을 더한다:

```markdown
- [ ] **초기화하는 계정이 `db_owner`인지 확인한다.** 트리거를 `dbo` 권한으로 실행하도록 만들기 때문에
      `dbo`를 가장할 수 있어야 한다. 권한이 부족하면 초기화가 실패하고 사유가 그대로 표시된다.
```

문제 해결 표에 행을 더한다:

```markdown
| 창 위쪽에 "변경 추적기가 구버전입니다"가 뜬다 | 0.2.6 이전에 초기화한 데이터베이스다. **추적기 업데이트** 를 누른 뒤 **전체 다시 추출** 을 한 번 실행한다 |
```

- [ ] **Step 4: 전체 테스트와 빌드**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: 전부 PASS

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/source.extension.vsixmanifest README.md docs/setup-checklist.md
git commit -m "docs: 추적기 업데이트와 인덱스 추적을 문서에 반영한다"
```

---

## 수동 검증 (CI가 하지 못하는 것)

구현이 끝나면 SSMS 21에서 직접 확인한다. 아래를 통과하기 전에는 "동작한다"고 말할 수 없다.

- [ ] 0.2.5로 초기화해 둔 데이터베이스에 접속 → 구버전 안내와 버튼이 뜬다
- [ ] **추적기 업데이트** 클릭 → 완료 알림에 "전체 다시 추출" 안내가 있고, 진행 중에도 쿼리 편집기가 그대로 동작한다
- [ ] 테이블에 인덱스를 만들고 **새로고침** → 그 테이블이 "수정"으로 뜨고 비교창 오른쪽에 인덱스가 보인다
- [ ] 인덱스를 지우고 **새로고침** → 테이블이 "수정"으로 뜬다. **"삭제"가 아니어야 한다**
- [ ] `GRANT SELECT ON dbo.어떤테이블 TO public` 실행 후 **새로고침** → 목록에 아무것도 늘지 않는다
- [ ] `db_owner`가 아닌 계정으로 테이블을 만들어 본다 → 성공한다
