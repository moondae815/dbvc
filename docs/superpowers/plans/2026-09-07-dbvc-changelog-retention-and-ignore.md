# ChangeLog 보존과 "무시" 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `DBVC_ChangeLog`가 30일 규칙 하나로 스스로 줄고, 사용자가 "무시" 한 번으로 파일과 로그 행을 함께 치울 수 있게 한다.

**Architecture:** 정리는 대상 DB 안의 저장 프로시저(`DBVC_PurgeChangeLog`)가 하고 클라이언트는 새로고침 때 `EXEC` 한 줄만 부른다. 도구가 자기 객체를 추적·추출하지 않도록 자기 제외를 `DBVC_` 접두사 규칙 한 곳(`DbvcOwnedObjects`)으로 모은다. "무시"는 새 기능이 아니라 기존 `DiscardChanges`와 `MarkProcessed`의 합성이다.

**Tech Stack:** C# (netstandard2.0 + net48), WPF/MVVM, NUnit, Moq, LibGit2Sharp, SMO, T-SQL

**Spec:** [`docs/superpowers/specs/2026-09-07-dbvc-changelog-retention-and-ignore-design.md`](../specs/2026-09-07-dbvc-changelog-retention-and-ignore-design.md)

**브랜치:** `feat/changelog-retention-and-ignore` (이미 만들어져 있고 스펙 커밋이 올라가 있다)

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip 포함. Core는 상태를 영어 식별자로 다루고 화면 계층에서만 한국어로 옮긴다.
- **주석은 "왜"만 적는다.** 한국어 평서문. 함정과 근거를 남기는 기존 문체를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): 변경 로그를 30일로 정리한다`
- **TDD**: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **테스트 이름은 영어 `Method_Result_WhenCondition`.** 예외: `InstallScriptSyncTests`는 그 파일의 기존 서술형(`InstallScript_StampsTheVersionCoreRequires`)을 따른다.
- **보존 기간은 30일 고정 상수.** 설정으로 빼지 않는다(스펙 2.1).
- **삭제 배치 크기는 5000.**
- **스키마 버전은 6.**
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `SqlManagementObjects 171.30.0`은 SSMS 21에 맞춘 값이다.
- **테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다.** 전이 참조로만 받는다.
- `MessageBox`로 나가는 문구에 백틱을 쓰지 않는다 — 마크다운이 렌더링되지 않아 문자 그대로 보인다.

**빌드·테스트 명령**

```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests
dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DbvcOwnedObjects"
```

SQL Server가 없으면 `DdlTriggerIntegrationTests`와 `SmoManagerIntegrationTests`는 실패가 아니라 Skip이다.

---

### Task 1: `DbvcOwnedObjects` — 자기 제외 판정 한 곳

**Files:**
- Create: `src/DBVC.Core/DbvcOwnedObjects.cs`
- Test: `tests/DBVC.Core.Tests/DbvcOwnedObjectsTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public const string DbvcOwnedObjects.Prefix = "DBVC_"`
  - `public const string DbvcOwnedObjects.TriggerName = "trg_DBVC_DDL_Tracker"`
  - `public static bool DbvcOwnedObjects.IsOwned(string? objectName)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/DbvcOwnedObjectsTests.cs`:

```csharp
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 이름을 하나씩 보태는 방식을 접두사 규칙으로 바꾼 판정. 설치 스크립트의
    /// LIKE N'DBVC[_]%'와 같은 결과를 내야 하며, 어긋나면 도구가 자기 객체를
    /// 저장소에 커밋하거나 사용자 객체를 조용히 추적에서 뺀다.
    /// </summary>
    [TestFixture]
    public class DbvcOwnedObjectsTests
    {
        [Test]
        public void IsOwned_ReturnsTrue_WhenNameStartsWithDbvcPrefix()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DbvcOwnedObjects.IsOwned("DBVC_ChangeLog"), Is.True);
                Assert.That(DbvcOwnedObjects.IsOwned("DBVC_PurgeChangeLog"), Is.True);
            });
        }

        [Test]
        public void IsOwned_ReturnsTrue_WhenNameIsTheDdlTrigger()
        {
            // 트리거만 접두사를 따르지 않는다. 이름을 바꾸면 기존 설치와 어긋난다.
            Assert.That(DbvcOwnedObjects.IsOwned("trg_DBVC_DDL_Tracker"), Is.True);
        }

        [Test]
        public void IsOwned_IsCaseInsensitive()
        {
            // 데이터 정렬이 대소문자를 구분하는 서버에서도 판정은 같아야 한다.
            Assert.That(DbvcOwnedObjects.IsOwned("dbvc_changelog"), Is.True);
        }

        [Test]
        public void IsOwned_ReturnsFalse_WhenPrefixIsNotFollowedByUnderscore()
        {
            // SQL 쪽 LIKE의 [_] 이스케이프와 같은 판정이다. 밑줄을 와일드카드로 두면
            // 사용자의 DBVCx 객체까지 추적에서 빠진다.
            Assert.Multiple(() =>
            {
                Assert.That(DbvcOwnedObjects.IsOwned("DBVCx_Table"), Is.False);
                Assert.That(DbvcOwnedObjects.IsOwned("DBVCReport"), Is.False);
            });
        }

        [Test]
        public void IsOwned_ReturnsFalse_WhenNameIsNullOrBlank()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DbvcOwnedObjects.IsOwned(null), Is.False);
                Assert.That(DbvcOwnedObjects.IsOwned("   "), Is.False);
            });
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DbvcOwnedObjects"`
Expected: 컴파일 실패 — `DbvcOwnedObjects` 형식을 찾을 수 없다

- [ ] **Step 3: 최소 구현**

`src/DBVC.Core/DbvcOwnedObjects.cs`:

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// DBVC가 대상 DB에 만드는 객체를 가른다. 추적하지도(DDL 트리거) 추출하지도(SMO) 않는다.
    ///
    /// 이름을 하나씩 보태지 않는 이유는, 객체가 늘 때마다 SQL 트리거와 SmoManager 두 곳에
    /// 같은 이름을 더해야 하고 한쪽을 빠뜨리면 도구가 자기 자신을 저장소에 커밋하기 때문이다.
    /// 접두사를 도구의 이름공간으로 선언해 그 실수를 구조적으로 없앤다.
    ///
    /// 대가: 사용자가 DBVC_로 시작하는 객체를 만들면 조용히 추적에서 빠진다. README에 적는다.
    /// </summary>
    public static class DbvcOwnedObjects
    {
        /// <summary>설치 스크립트의 LIKE N'DBVC[_]%'와 같은 값이어야 한다. InstallScriptSyncTests가 대조한다.</summary>
        public const string Prefix = "DBVC_";

        /// <summary>트리거만 접두사 규칙 밖에 있다. 기존 설치와 이름이 같아야 하므로 바꾸지 않는다.</summary>
        public const string TriggerName = "trg_DBVC_DDL_Tracker";

        public static bool IsOwned(string? objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return false;

            // StartsWith가 밑줄까지 함께 요구하므로 SQL의 [_] 이스케이프와 결과가 같다.
            return objectName!.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(objectName, TriggerName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DbvcOwnedObjects"`
Expected: PASS (5개)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/DbvcOwnedObjects.cs tests/DBVC.Core.Tests/DbvcOwnedObjectsTests.cs
git commit -m "feat(core): DBVC 소유 객체를 접두사 규칙으로 가른다"
```

---

### Task 2: `SmoManager`가 접두사 규칙을 쓴다

지금 `SmoManager.cs:622`는 `DBVC_ChangeLog` **테이블 하나만** 이름으로 제외한다. 프로시저·뷰·함수 쪽에는 제외가 없어서, Task 4가 더할 프로시저가 "전체 다시 추출"에 잡혀 저장소에 커밋된다.

`ShouldInclude`가 `EnumerateTargets`의 결과를 전부 통과시키는 유일한 관문(`:322`)이라 거기 한 줄이면 9개 타입 루프가 모두 덮인다. 그리고 그 함수는 `internal static`이라 SMO 없이 단위 테스트할 수 있다.

**Files:**
- Modify: `src/DBVC.Core/SmoManager.cs:603-607` (`ShouldInclude`), `src/DBVC.Core/SmoManager.cs:622` (테이블 루프)
- Test: `tests/DBVC.Core.Tests/SmoManagerTests.cs` (기존 `ShouldInclude_*` 테스트 옆)

**Interfaces:**
- Consumes: `DbvcOwnedObjects.IsOwned(string?)` (Task 1)
- Produces: 없음 (동작 변경만)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/SmoManagerTests.cs`의 기존 `ShouldInclude_IsCaseInsensitive` 아래에 더한다. 헬퍼 `Target(schema, type, name)`은 그 파일에 이미 있다.

```csharp
        [Test]
        public void ShouldInclude_ExcludesDbvcOwnedObjects_WhenNoFilterGiven()
        {
            // "전체 다시 추출"은 필터 없이 돈다. 여기서 거르지 않으면 도구가 만든
            // 프로시저가 dbo/StoredProcedures/DBVC_PurgeChangeLog.sql로 커밋된다.
            Assert.That(
                SmoManager.ShouldInclude(Target("dbo", "StoredProcedure", "DBVC_PurgeChangeLog"), null),
                Is.False);
        }

        [Test]
        public void ShouldInclude_ExcludesDbvcOwnedObjects_EvenWhenTheFilterAsksForThem()
        {
            // 필터는 로그에서 온다. 구버전이 남긴 행이 DBVC 객체를 가리켜도
            // 그것이 저장소에 써지는 일은 없어야 한다.
            var filter = new HashSet<string>(
                new[] { "dbo.DBVC_PurgeChangeLog" }, StringComparer.OrdinalIgnoreCase);

            Assert.That(
                SmoManager.ShouldInclude(Target("dbo", "StoredProcedure", "DBVC_PurgeChangeLog"), filter),
                Is.False);
        }

        [Test]
        public void ShouldInclude_KeepsUserObjects_WhenNameOnlyResemblesThePrefix()
        {
            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "DBVCReport"), null), Is.True);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~ShouldInclude"`
Expected: 새 테스트 3개 중 2개 FAIL (`Expected: False But was: True`)

- [ ] **Step 3: 최소 구현**

`src/DBVC.Core/SmoManager.cs`의 `ShouldInclude`를 바꾼다:

```csharp
        internal static bool ShouldInclude(ScriptTargetInfo target, HashSet<string>? filter)
        {
            // 필터보다 먼저 본다. 필터는 DDL 로그에서 오므로 구버전이 남긴 행이 DBVC
            // 객체를 가리킬 수 있고, 그때도 저장소에는 써지지 않아야 한다.
            if (DbvcOwnedObjects.IsOwned(target.Name)) return false;

            if (filter == null) return true;
            return filter.Contains(target.QualifiedName) || filter.Contains(target.Name);
        }
```

`:622`의 리터럴 비교도 같은 함수로 바꾼다. **이 `continue`는 지우지 않는다** — `ShouldInclude`는 테이블만 거르고, 그 테이블 밑의 DML 트리거 열거까지 막는 것은 이 자리뿐이다:

```csharp
                if (table.IsSystemObject) continue;
                // 테이블 자신뿐 아니라 그 밑의 DML 트리거 열거까지 건너뛴다.
                // ShouldInclude가 뒤에서 한 번 더 거르지만 자식까지 막는 것은 여기다.
                if (DbvcOwnedObjects.IsOwned(table.Name)) continue;
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SmoManagerTests"`
Expected: PASS. 기존 `ShouldInclude_*` 4개도 그대로 통과해야 한다.

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/SmoManager.cs tests/DBVC.Core.Tests/SmoManagerTests.cs
git commit -m "fix(core): 추출이 DBVC 소유 객체를 저장소에 쓰지 않게 한다"
```

---

### Task 3: 설치 스크립트의 자기 제외를 접두사로 바꾼다

**Files:**
- Modify: `src/DBVC.Database/InstallTrigger.sql` (트리거 본문의 `@ObjectName` 검사)
- Test: `tests/DBVC.Core.Tests/InstallScriptSyncTests.cs`

**Interfaces:**
- Consumes: `DbvcOwnedObjects.Prefix`, `DbvcOwnedObjects.TriggerName` (Task 1)
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/InstallScriptSyncTests.cs`에 더한다. 이 파일은 서술형 이름 규칙을 쓴다.

```csharp
        [Test]
        public void InstallScript_ExcludesTheSameObjectsCoreCallsItsOwn()
        {
            // 트리거는 SQL이라 DbvcOwnedObjects를 부를 수 없다. 두 판정이 갈라지면
            // 도구가 자기 DDL을 사용자 변경으로 기록하고, 그것이 저장소에 커밋된다.
            var script = StateTracker.ReadInstallScript();

            // "DBVC_" → "DBVC[_]" → N'DBVC[_]%'. 밑줄은 LIKE의 와일드카드라 이스케이프한다.
            var expectedPattern = "N'" + DbvcOwnedObjects.Prefix.Replace("_", "[_]") + "%'";

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain(expectedPattern),
                    "설치 스크립트의 접두사 패턴이 DbvcOwnedObjects.Prefix와 다릅니다");
                Assert.That(script, Does.Contain("N'" + DbvcOwnedObjects.TriggerName + "'"),
                    "설치 스크립트가 DDL 트리거 이름을 제외 목록에 두지 않았습니다");
            });
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~InstallScriptSyncTests"`
Expected: FAIL — `N'DBVC[_]%'`를 찾지 못한다

- [ ] **Step 3: 최소 구현**

`src/DBVC.Database/InstallTrigger.sql`의 트리거 본문에서 아래 두 줄을 찾는다:

```sql
    -- DBVC 자체 테이블/트리거에 대한 DDL은 사용자 변경이 아니므로 기록하지 않는다.
    IF @ObjectName IS NULL OR @ObjectName IN (N'DBVC_ChangeLog', N'trg_DBVC_DDL_Tracker')
        RETURN;
```

이렇게 바꾼다:

```sql
    -- DBVC 자체 객체에 대한 DDL은 사용자 변경이 아니므로 기록하지 않는다.
    -- 이름을 하나씩 나열하지 않는 이유는 객체가 늘 때마다 여기와 SmoManager 두 곳을
    -- 함께 고쳐야 하고, 한쪽을 빠뜨리면 도구가 자기 자신을 저장소에 커밋하기 때문이다.
    -- DbvcOwnedObjects와 같은 판정이어야 하며 InstallScriptSyncTests가 대조한다.
    -- LIKE의 [_]는 밑줄이 와일드카드이기 때문이다 - 빼면 DBVCx로 시작하는 사용자 객체까지 빠진다.
    IF @ObjectName IS NULL OR @ObjectName LIKE N'DBVC[_]%' OR @ObjectName = N'trg_DBVC_DDL_Tracker'
        RETURN;
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~InstallScriptSyncTests"`
Expected: PASS (4개)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Database/InstallTrigger.sql tests/DBVC.Core.Tests/InstallScriptSyncTests.cs
git commit -m "fix(db): 트리거의 자기 제외를 접두사 규칙으로 바꾼다"
```

---

### Task 4: 정리 프로시저·인덱스와 스키마 v6

**Files:**
- Modify: `src/DBVC.Database/InstallTrigger.sql` (인덱스, 프로시저, GRANT, 버전 값)
- Modify: `src/DBVC.Core/StateTracker.cs:22` (`RequiredSchemaVersion`)
- Test: `tests/DBVC.Core.Tests/StateTrackerTests.cs:649` (기존 테스트 이름과 값)

**Interfaces:**
- Consumes: 없음
- Produces: 대상 DB의 `dbo.DBVC_PurgeChangeLog` 프로시저 (Task 5·9가 부른다)

- [ ] **Step 1: 기존 테스트를 v6로 고쳐 실패시킨다**

`tests/DBVC.Core.Tests/StateTrackerTests.cs:649`의 테스트는 값 `5`를 리터럴로 단언하는 **유일한** 자리다. 이름과 값을 함께 바꾼다:

```csharp
        [Test]
        public void RequiredSchemaVersion_IsSix()
        {
            // 설치 스크립트가 심는 값과 같아야 한다. 어긋나면 모든 사용자에게 업데이트 배너가 계속 뜨거나
            // 구버전이 최신으로 읽힌다. 스크립트 쪽 값은 InstallScriptSyncTests가 대조한다.
            Assert.That(StateTracker.RequiredSchemaVersion, Is.EqualTo(6));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RequiredSchemaVersion"`
Expected: FAIL — `Expected: 6 But was: 5`

- [ ] **Step 3: 상수를 올린다**

`src/DBVC.Core/StateTracker.cs:22`:

```csharp
        public const int RequiredSchemaVersion = 6;
```

- [ ] **Step 4: 스크립트 쪽 동기화 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~InstallScript_StampsTheVersionCoreRequires"`
Expected: FAIL — 스크립트는 아직 `5`를 심는다. **이 실패가 스크립트를 함께 고치라는 신호다.**

- [ ] **Step 5: 설치 스크립트에 인덱스와 프로시저를 더하고 버전을 올린다**

`src/DBVC.Database/InstallTrigger.sql`에서 기존 `IX_DBVC_ChangeLog_IsProcessed` 인덱스 블록 **바로 뒤**에 인덱스 하나를 더한다:

```sql
-- 정리(DBVC_PurgeChangeLog)의 조회 경로. PostTime 단독 조건은 위 인덱스로 seek이 되지 않아
-- 인덱스가 없으면 정리가 매번 전체 스캔이 된다.
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND name = N'IX_DBVC_ChangeLog_PostTime')
BEGIN
    CREATE NONCLUSTERED INDEX [IX_DBVC_ChangeLog_PostTime]
        ON [dbo].[DBVC_ChangeLog] ([PostTime]);
END
GO
```

기존 `GRANT SELECT, UPDATE ...` 블록 **바로 뒤**에 프로시저와 그 GRANT를 더한다:

```sql
-- 변경 로그 정리. 나이 하나로 지우고 IsProcessed를 보지 않는다 - 커밋되지 않은 채 남는
-- 미채택자의 행이 정확히 IsProcessed = 0이라, 처리된 행만 지우는 정책은 그 행에 영영 닿지 않는다.
--
-- EXECUTE AS OWNER인 이유는 public에 DELETE를 주지 않기 위해서다. DELETE를 주면 사용자가
-- 로그를 직접 조작할 수 있게 되고, 그것은 위 GRANT에서 INSERT를 뺀 이유와 같은 문제다.
--
-- 배치로 나누는 이유는 첫 실행 때문이다. 몇 달 쌓인 DB에서 한 트랜잭션으로 수백만 행을
-- 지우면 그동안 모든 DDL이 트리거의 INSERT에서 막히고 트랜잭션 로그가 부풀어 오른다.
--
-- CREATE OR ALTER를 쓰지 않는 것은 그것이 SQL Server 2016 SP1+를 요구해 최소 버전을
-- 새로 못 박기 때문이다. 트리거와 같은 DROP -> CREATE 형태를 쓴다.
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_PurgeChangeLog]') AND type = N'P')
BEGIN
    DROP PROCEDURE [dbo].[DBVC_PurgeChangeLog];
END
GO

CREATE PROCEDURE [dbo].[DBVC_PurgeChangeLog]
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    -- DBVC_RETENTION_DAYS: Core의 StateTracker.RetentionDays와 같아야 한다.
    -- InstallScriptSyncTests가 두 값을 대조한다.
    DECLARE @cutoff DATETIME = DATEADD(day, -30, GETDATE());
    DECLARE @deleted INT = 1;

    WHILE @deleted > 0
    BEGIN
        DELETE TOP (5000) FROM [dbo].[DBVC_ChangeLog] WHERE [PostTime] < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END
END
GO

-- 클라이언트는 접속 계정 그대로 이것을 부른다. EXECUTE만 주므로 사용자가 지울 수 있는 것은
-- 프로시저가 허용하는 것뿐이다.
GRANT EXECUTE ON [dbo].[DBVC_PurgeChangeLog] TO [public];
GO
```

같은 파일의 확장 속성 값 둘(`sp_addextendedproperty`와 `sp_updateextendedproperty`)을 `N'5'` → `N'6'`으로 바꾼다.

- [ ] **Step 6: 보존 기간 상수를 Core에 두고 동기화 테스트를 더한다**

`src/DBVC.Core/StateTracker.cs`의 `RequiredSchemaVersion` 아래에 더한다:

```csharp
        /// <summary>
        /// 변경 로그 보존 기간. 설치 스크립트의 DBVC_RETENTION_DAYS 표식과 같아야 한다.
        ///
        /// 설정으로 빼지 않는다 - DB마다 값이 달라지면 그 이유를 아무도 기억하지 못하고,
        /// 화면에 드러나지 않아 "이 DB는 왜 다르게 동작하지"를 진단할 길이 없다.
        /// </summary>
        public const int RetentionDays = 30;
```

`tests/DBVC.Core.Tests/InstallScriptSyncTests.cs`에 더한다:

```csharp
        [Test]
        public void InstallScript_PurgesAfterTheRetentionCoreDeclares()
        {
            // 두 값이 갈라지면 문서와 실제 동작이 달라진다 - 30일이라 적어 놓고 90일에 지운다.
            var script = StateTracker.ReadInstallScript();
            var match = Regex.Match(script, @"DATEADD\(day,\s*-(\d+),\s*GETDATE\(\)\)");

            Assert.That(match.Success, Is.True, "설치 스크립트에서 보존 기간을 찾지 못했습니다");
            Assert.That(int.Parse(match.Groups[1].Value), Is.EqualTo(StateTracker.RetentionDays));
        }
```

- [ ] **Step 7: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS. SQL Server가 있으면 `DdlTriggerIntegrationTests`도 통과해야 한다 — 그 픽스처는 실제 설치 경로를 그대로 타므로 **새 SQL의 구문 오류가 여기서 잡힌다.** 없으면 Skip이고, Task 9에서 확인한다.

- [ ] **Step 8: 커밋**

```bash
git add src/DBVC.Database/InstallTrigger.sql src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/StateTrackerTests.cs tests/DBVC.Core.Tests/InstallScriptSyncTests.cs
git commit -m "feat(db): 변경 로그를 30일로 정리하는 프로시저를 더하고 스키마를 v6로 올린다"
```

---

### Task 5: 새로고침이 정리를 부른다

**Files:**
- Modify: `src/DBVC.Core/StateTracker.cs` (`RefreshState` 본문, 새 상수와 private 메서드)
- Test: `tests/DBVC.Core.Tests/StateTrackerTests.cs`

**Interfaces:**
- Consumes: 대상 DB의 `dbo.DBVC_PurgeChangeLog` (Task 4)
- Produces: `internal const string StateTracker.PurgeCommand`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/StateTrackerTests.cs`에 더한다:

```csharp
        [Test]
        public void PurgeCommand_CallsTheProcedureTheInstallScriptCreates()
        {
            // 이름이 어긋나면 정리가 영영 돌지 않는데, 실패를 삼키는 자리라 아무도 모른다.
            var script = StateTracker.ReadInstallScript();

            Assert.Multiple(() =>
            {
                Assert.That(StateTracker.PurgeCommand, Does.Contain("DBVC_PurgeChangeLog"));
                Assert.That(script, Does.Contain("CREATE PROCEDURE [dbo].[DBVC_PurgeChangeLog]"));
            });
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PurgeCommand"`
Expected: 컴파일 실패 — `StateTracker.PurgeCommand`가 없다

- [ ] **Step 3: 최소 구현**

`src/DBVC.Core/StateTracker.cs`의 `MarkProcessedCommand` 선언 아래에 더한다:

```csharp
        /// <summary>
        /// 보존 기간이 지난 로그 행을 지운다. 정책은 전부 프로시저 안에 있고 클라이언트는
        /// 부르기만 한다 - public에 DELETE를 주지 않으려면 그래야 한다.
        /// </summary>
        internal const string PurgeCommand = "EXEC dbo.DBVC_PurgeChangeLog";
```

같은 파일에 private 메서드를 더한다(`ReadCurrentAuthor` 옆이 자연스럽다):

```csharp
        /// <summary>
        /// 오래된 로그를 정리한다. 실패는 삼킨다 - 구버전(v6 이전) DB에는 프로시저가 없어
        /// 반드시 실패하고, 그 경우 화면에는 이미 업데이트 안내가 따로 떠 있다.
        /// 정리하지 못하는 것이 새로고침을 무너뜨릴 이유는 되지 않는다(ReconcileWithDatabase와 같은 관용).
        /// </summary>
        private static void TryPurge(string connectionString)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = PurgeCommand;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StateTracker.TryPurge skipped: {ex.Message}");
            }
        }
```

`RefreshState`에서 연결 문자열을 만든 직후, `ReadPendingRows` **앞에서** 부른다. 앞에서 부르는 이유는 방금 지워진 행이 같은 새로고침의 목록에 뜨지 않게 하기 위해서다:

```csharp
                var connectionString = BuildConnectionString(serverName, databaseName);

                // 읽기 전에 정리한다. 뒤에 두면 방금 지울 행이 이번 목록에 한 번 더 뜬다.
                TryPurge(connectionString);

                // 좁힐 때도 전체를 읽는다. 남이 만진 경로가 무엇인지 알아야 Git 폴백이 그것을
                // 도로 넣지 않는다 - 추출은 작업자를 가리지 않으므로 남의 .sql도 더럽게 보인다.
                rows = ReadPendingRows(connectionString);
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/StateTracker.cs tests/DBVC.Core.Tests/StateTrackerTests.cs
git commit -m "feat(core): 새로고침이 오래된 변경 로그를 정리한다"
```

---

### Task 6: 표면화를 계약으로 고정한다

스펙 2.2가 기대는 성질을 테스트로 못 박는다. **이 테스트는 처음부터 통과한다** — 지금 코드가 이미 그렇게 동작하기 때문이다. 실패를 기대하지 말 것. 목적은 다음 사람이 Git 폴백을 최적화하다 조용히 깨는 것을 막는 데 있다.

**Files:**
- Test: `tests/DBVC.Core.Tests/StateTrackerTests.cs` (`// ---------- 변경 집합 구성 ----------` 구역)

**Interfaces:**
- Consumes: `StateTracker.BuildChangeSet` (기존)
- Produces: 없음

- [ ] **Step 1: 계약 테스트를 쓴다**

```csharp
        [Test]
        public void BuildChangeSet_SurfacesForeignFile_WhenLogRowIsGone()
        {
            // 30일 정리가 남의 열린 행을 지우면 그 경로는 PartitionByAuthor의 foreignPaths에서도
            // 빠진다. 그때부터 Git 폴백이 그 더러운 파일을 주인 없는 변경으로 올린다.
            //
            // 이것이 미채택자의 변경을 결국 git에 담기게 하는 유일한 길이다(설계 2.2).
            // LastLogId가 0이라 되돌리기가 자기 취소되지도 않는다. 우연에 기대지 않도록 여기서 고정한다.
            var tracker = NewTracker();
            var gitStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo/Tables/Foo.sql"] = "Modified"
            };

            var changes = tracker.BuildChangeSet(
                new List<ChangeLogRow>(), gitStates, foreignPaths: null);

            Assert.Multiple(() =>
            {
                Assert.That(changes.Count, Is.EqualTo(1));
                Assert.That(changes[0].RelativePath, Is.EqualTo("dbo/Tables/Foo.sql"));
                Assert.That(changes[0].LastLogId, Is.EqualTo(0));
            });
        }

        [Test]
        public void BuildChangeSet_HidesForeignFile_WhileTheLogRowIsStillOpen()
        {
            // 정리 전에는 가려져 있어야 한다. 이 짝이 없으면 위 테스트가 "언제나 뜬다"로 읽힌다.
            var tracker = NewTracker();
            var gitStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo/Tables/Foo.sql"] = "Modified"
            };

            var changes = tracker.BuildChangeSet(
                new List<ChangeLogRow>(), gitStates,
                foreignPaths: new[] { "dbo/Tables/Foo.sql" });

            Assert.That(changes, Is.Empty);
        }
```

- [ ] **Step 2: 통과를 확인한다 (실패를 기대하지 않는다)**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~BuildChangeSet"`
Expected: PASS. 실패하면 표면화 계약이 이미 깨진 것이므로 **멈추고 보고한다** — 이 계획의 전제가 무너진 상황이다.

- [ ] **Step 3: 커밋**

```bash
git add tests/DBVC.Core.Tests/StateTrackerTests.cs
git commit -m "test(core): 정리 뒤 남의 파일이 떠오르는 계약을 고정한다"
```

---

### Task 7: 무시의 확인 문구와 요약

문구 조립을 `internal static`으로 빼 SQL·WPF 없이 검증한다. `BuildDiscardConfirmation`과 같은 자리(`ViewChangesViewModel`)에 두는 이유는 그것이 가장 가까운 선례이기 때문이다. `DBVC.Vsix`는 이미 `InternalsVisibleTo("DBVC.Vsix.Tests")`를 선언한다(`DbvcVersion.cs:8`).

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`BuildDiscardSummary` 아래)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/IgnoreMessageTests.cs` (Create)

**Interfaces:**
- Consumes: `DiscardPlan` (`RestorePaths`, `DeletePaths`), `DiscardResult` (`RestoredPaths`, `DeletedPaths`, `SkippedPaths`, `FailedPaths`)
- Produces:
  - `internal static string ViewChangesViewModel.BuildIgnoreConfirmation(DiscardPlan plan, int rowsToClose, IReadOnlyList<string> foreignAuthors)`
  - `internal static string ViewChangesViewModel.BuildIgnoreSummary(DiscardResult result, int closedRows)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/IgnoreMessageTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 무시는 되돌리기보다 훨씬 센 동작이다 - 로그 행을 닫는 것은 공유 DB에서 전역이고,
    /// 닫힌 변경은 git에 영영 담기지 않는다. 그 사실이 확인 문구에서 빠지지 않게 한다.
    /// </summary>
    [TestFixture]
    public class IgnoreMessageTests
    {
        private static DiscardPlan PlanWith(string restore, string delete)
        {
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [restore] = "Modified",
                [delete] = "Added"
            };

            return DiscardPlan.Build(new[] { restore, delete }, states);
        }

        [Test]
        public void BuildIgnoreConfirmation_MentionsGlobalEffect_WhenRowsWillClose()
        {
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 2,
                foreignAuthors: new List<string>());

            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("함께 쓰는 모두에게"));
                Assert.That(text, Does.Contain("git"));
                // DDL을 취소하는 것으로 읽히면 안 된다.
                Assert.That(text, Does.Contain("데이터베이스의 변경은 그대로 남습니다"));
            });
        }

        [Test]
        public void BuildIgnoreConfirmation_OmitsGlobalEffect_WhenNothingWillClose()
        {
            // 닫을 행이 없으면 되돌리기와 같은 무게다. 없는 위험을 경고하면 문구가 값을 잃는다.
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 0,
                foreignAuthors: new List<string>());

            Assert.That(text, Does.Not.Contain("함께 쓰는 모두에게"));
        }

        [Test]
        public void BuildIgnoreConfirmation_CountsForeignItems_WhenOthersChangesSelected()
        {
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 2,
                foreignAuthors: new List<string> { "CORP\\jdoe" });

            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("다른 사람"));
                Assert.That(text, Does.Contain("CORP\\jdoe"));
            });
        }

        [Test]
        public void BuildIgnoreConfirmation_NamesFilesThatCannotBeRecovered()
        {
            // 미추적 파일은 지워지고 git이 갖고 있지 않다. 셈만으로는 부족하다.
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 1,
                foreignAuthors: new List<string>());

            Assert.That(text, Does.Contain("dbo/Views/Bar.sql"));
        }

        [Test]
        public void BuildIgnoreSummary_ReportsClosedRows_WhenRowsWereClosed()
        {
            var result = new DiscardResult();
            result.RestoredPaths.Add("dbo/Tables/Foo.sql");

            var text = ViewChangesViewModel.BuildIgnoreSummary(result, closedRows: 3);

            Assert.Multiple(() =>
            {
                Assert.That(text, Does.StartWith("무시했습니다"));
                Assert.That(text, Does.Contain("로그 3개 닫음"));
            });
        }

        [Test]
        public void BuildIgnoreSummary_DoesNotClaimSuccess_WhenNothingChanged()
        {
            var result = new DiscardResult();
            result.SkippedPaths.Add("dbo/Tables/Foo.sql");

            var text = ViewChangesViewModel.BuildIgnoreSummary(result, closedRows: 0);

            Assert.That(text, Does.Not.StartWith("무시했습니다"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~IgnoreMessageTests"`
Expected: 컴파일 실패 — `BuildIgnoreConfirmation`이 없다

- [ ] **Step 3: 최소 구현**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`의 `BuildDiscardSummary` 아래에 더한다:

```csharp
        /// <summary>
        /// 무시의 확인 문구. 되돌리기보다 한 문단 무겁다 - 로그 행을 닫는 것은 공유 DB에서
        /// 전역이고, 닫힌 변경은 git에 영영 담기지 않기 때문이다.
        ///
        /// 순수 함수로 둔 이유는 SQL Server와 WPF 없이 검증하기 위해서다
        /// (BuildMarkProcessedFailureMessage를 Core로 뺀 것과 같은 이유).
        /// </summary>
        /// <param name="rowsToClose">닫을 로그 행이 있는 항목 수. 0이면 전역 경고를 넣지 않는다.</param>
        /// <param name="foreignAuthors">선택 항목의 작업자 중 현재 사용자가 아닌 사람들. 중복 없이 온다.</param>
        internal static string BuildIgnoreConfirmation(
            DiscardPlan plan, int rowsToClose, IReadOnlyList<string> foreignAuthors)
        {
            var nl = Environment.NewLine;
            var builder = new StringBuilder();
            builder.Append("선택한 변경을 무시합니다.").Append(nl).Append(nl);

            if (plan.RestorePaths.Count > 0)
            {
                builder.Append($"  되돌릴 파일 {plan.RestorePaths.Count}개").Append(nl);
            }

            if (plan.DeletePaths.Count > 0)
            {
                builder.Append($"  지울 파일 {plan.DeletePaths.Count}개 — git으로는 복구되지 않습니다").Append(nl);
                foreach (var path in plan.DeletePaths.Take(MaxListedDeletePaths))
                {
                    builder.Append("    · ").Append(path).Append(nl);
                }

                var rest = plan.DeletePaths.Count - MaxListedDeletePaths;
                if (rest > 0) builder.Append($"    외 {rest}개").Append(nl);
            }

            if (rowsToClose > 0)
            {
                builder.Append($"  닫을 변경 로그 {rowsToClose}개").Append(nl);
            }

            if (foreignAuthors.Count > 0)
            {
                builder.Append(nl)
                    .Append($"선택한 항목에는 다른 사람({string.Join(", ", foreignAuthors)})의 변경이 들어 있습니다.")
                    .Append(nl);
            }

            // 닫을 행이 없으면 되돌리기와 같은 무게다. 없는 위험을 경고하면 문구가 값을 잃는다.
            if (rowsToClose > 0)
            {
                builder.Append(nl)
                    .Append("변경 로그를 닫는 것은 이 데이터베이스를 함께 쓰는 모두에게 적용됩니다.").Append(nl)
                    .Append("닫힌 변경은 앞으로 어떤 새로고침에도 다시 나타나지 않고, git에 담기지 않습니다.").Append(nl);
            }

            builder.Append(nl)
                .Append("데이터베이스의 변경은 그대로 남습니다 — 무시는 DDL을 취소하지 않습니다.")
                .Append(nl).Append(nl)
                .Append("계속할까요?");

            return builder.ToString();
        }

        /// <summary>
        /// 무시의 결과 요약. 되돌리거나 지우거나 닫은 것이 하나도 없으면 성공 어투를 쓰지 않는다 —
        /// 제외·실패뿐인 결과가 성공처럼 읽히면 사용자는 치워진 줄 안다.
        /// </summary>
        internal static string BuildIgnoreSummary(DiscardResult result, int closedRows)
        {
            var parts = new List<string>();
            if (result.RestoredPaths.Count > 0) parts.Add($"되돌림 {result.RestoredPaths.Count}개");
            if (result.DeletedPaths.Count > 0) parts.Add($"삭제 {result.DeletedPaths.Count}개");
            if (closedRows > 0) parts.Add($"로그 {closedRows}개 닫음");
            if (result.SkippedPaths.Count > 0) parts.Add($"제외 {result.SkippedPaths.Count}개");
            if (result.FailedPaths.Count > 0) parts.Add($"실패 {result.FailedPaths.Count}개");

            if (parts.Count == 0) return "무시할 대상이 없습니다.";

            var succeeded = result.RestoredPaths.Count + result.DeletedPaths.Count + closedRows > 0;
            var lead = succeeded ? "무시했습니다 — " : "무시하지 못했습니다 — ";
            return lead + string.Join(", ", parts) + ".";
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~IgnoreMessageTests"`
Expected: PASS (6개)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/IgnoreMessageTests.cs
git commit -m "feat(vsix): 무시의 확인 문구와 요약을 더한다"
```

---

### Task 8: 무시 명령과 버튼

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`:121` 명령 등록, `:874` 속성, 되돌리기 구역 끝, `:2234` `RaiseCanExecuteChanged`)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml:191` (되돌리기 버튼 뒤)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `BuildIgnoreConfirmation`, `BuildIgnoreSummary` (Task 7), `IGitManager.DiscardChanges`, `IStateTracker.MarkProcessed`
- Produces: `public ICommand ViewChangesViewModel.IgnoreCommand`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`의 `// ---------- 되돌리기 ----------` 구역 끝에 더한다. 목 필드 이름은 이 파일의 것을 그대로 쓴다 — Git 목은 `_git`이고, `_notifier`는 Moq이 아니라 `RecordingNotifier`라 응답을 `ConfirmResult` 속성으로 준다.

기존 `NewViewModelWithSelectedChange`는 `LastLogId`를 채우지 않아(기본 0) 닫을 행이 없다. 무시 테스트는 열린 행이 있는 항목이 필요하므로 그 옆에 짝을 하나 만든다:

```csharp
        // ---------- 무시 ----------

        /// <summary>
        /// 열린 로그 행이 있는 변경 하나가 선택된 뷰모델. 되돌리기 쪽 헬퍼는 LastLogId가 0이라
        /// MarkProcessed가 스스로 건너뛴다 - 무시는 그 반대 경우를 봐야 한다.
        /// </summary>
        private ViewChangesViewModel NewViewModelWithOpenLogRow(
            string relativePath = "dbo/Tables/Users.sql", string state = "Modified")
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                new ChangeRecord
                {
                    QualifiedName = "dbo.Users", ObjectType = "TABLE", State = state,
                    RelativePath = relativePath, LastLogId = 42, Author = "sa", HostName = "PC-A"
                }
            });
            _git.Setup(g => g.GetChangedFileStates(It.IsAny<string>()))
                .Returns(new Dictionary<string, string> { [relativePath] = state });
            _git.Setup(g => g.DiscardChanges(Server, Database, It.IsAny<IEnumerable<string>>()))
                .Returns(new DiscardResult());

            var vm = NewConnectedViewModel();
            vm.Changes[0].IsSelected = true;
            return vm;
        }

        [Test]
        public void Ignore_ClosesLogRows_AfterDiscardSucceeds()
        {
            // 파일 먼저, 행 나중. 커밋 흐름과 같은 순서라 실패 문구를 그대로 쓸 수 있고,
            // 반대 순서는 행이 닫힌 채 더러운 파일이 남아 주인 없는 변경으로 떠오른다.
            var order = new List<string>();
            var vm = NewViewModelWithOpenLogRow();
            _notifier.ConfirmResult = true;

            _git.Setup(g => g.DiscardChanges(Server, Database, It.IsAny<IEnumerable<string>>()))
                .Callback(() => order.Add("discard"))
                .Returns(new DiscardResult());
            _stateTracker.Setup(s => s.MarkProcessed(Server, Database, It.IsAny<IEnumerable<ChangeRecord>>()))
                .Callback(() => order.Add("mark"))
                .Returns((string?)null);

            vm.IgnoreCommand.Execute(null);

            Assert.That(order, Is.EqualTo(new[] { "discard", "mark" }));
        }

        [Test]
        public void Ignore_DoesNotCloseRow_WhenDiscardFailedForThatPath()
        {
            // 되돌리지 못한 파일의 행을 닫으면 그 더러운 파일이 다음 새로고침에서
            // 주인 없는 변경으로 떠올라 사용자가 이해할 수 없는 상태가 된다.
            var vm = NewViewModelWithOpenLogRow();
            _notifier.ConfirmResult = true;

            var failed = new DiscardResult();
            failed.FailedPaths.Add("dbo/Tables/Users.sql");
            _git.Setup(g => g.DiscardChanges(Server, Database, It.IsAny<IEnumerable<string>>()))
                .Returns(failed);

            vm.IgnoreCommand.Execute(null);

            _stateTracker.Verify(
                s => s.MarkProcessed(Server, Database, It.Is<IEnumerable<ChangeRecord>>(r => r.Any())),
                Times.Never);
        }

        [Test]
        public void Ignore_DoesNotTouchRepository_WhenUserCancelsConfirmation()
        {
            var vm = NewViewModelWithOpenLogRow();
            _notifier.ConfirmResult = false;

            vm.IgnoreCommand.Execute(null);

            _git.Verify(g => g.DiscardChanges(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()), Times.Never);
            _stateTracker.Verify(s => s.MarkProcessed(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()), Times.Never);
        }

        [Test]
        public void Ignore_WarnsAboutTheGlobalEffect_InTheConfirmation()
        {
            // 이 문장이 빠지면 사용자는 무시를 되돌리기의 다른 이름으로 읽는다.
            var vm = NewViewModelWithOpenLogRow();
            _notifier.ConfirmResult = false;

            vm.IgnoreCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Single().Message, Does.Contain("함께 쓰는 모두에게"));
        }

        [Test]
        public void Ignore_DoesNotReExtract_AfterIgnoring()
        {
            // 되돌리기와 같은 이유다. 재추출하면 아직 닫지 못한 행이 가리키는 객체가
            // 다시 추출되어 같은 클릭 안에서 되돌리기가 취소된다.
            var vm = NewViewModelWithOpenLogRow();
            _notifier.ConfirmResult = true;
            _smo.Invocations.Clear();

            vm.IgnoreCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
                It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~ViewChangesViewModelTests.Ignore"`
Expected: 컴파일 실패 — `IgnoreCommand`가 없다

- [ ] **Step 3: 최소 구현**

`:121`의 `DiscardCommand` 등록 아래:

```csharp
            IgnoreCommand = new RelayCommand(Ignore, CanDiscard);
```

`:874`의 `DiscardCommand` 속성 아래:

```csharp
        public ICommand IgnoreCommand { get; }
```

되돌리기 구역(`BuildDiscardSummary` 위쪽, `DiscardOutcome` 선언 뒤)에 더한다:

```csharp
        // ---------- 무시 ----------

        private void Ignore() => Ignore(confirmed: false);

        /// <param name="confirmed">
        /// Discard와 같은 왕복 패턴이다. 판정은 저장소를 여는 일이라 백그라운드에서 하고
        /// 확인은 UI 스레드에서만 띄울 수 있어, 확인을 받은 뒤 참으로 해서 같은 경로를 다시 탄다.
        /// </param>
        private void Ignore(bool confirmed)
        {
            // 게이트는 되돌리기와 같다. 무시가 하는 일에 되돌리기가 포함되므로
            // 되돌릴 수 없는 상태에서 무시할 수 있어서는 안 된다.
            if (!CanDiscard()) return;

            var selectedPaths = Changes
                .Where(c => c.IsSelected)
                .Select(c => c.RelativePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();
            if (selectedPaths.Count == 0) return;

            var server = ServerName!;
            var database = DatabaseName!;
            var gitPath = _configManager.TryGetMapping(server, database)?.GitPath;
            if (gitPath == null) return;

            // 닫을 행은 화면 항목이 아니라 마지막 갱신의 레코드에서 온다 - LastLogId와 작업자는
            // 화면 항목에 없다. 커밋이 committedRecords를 고르는 것과 같은 자리다.
            var records = _lastChangeRecords
                .Where(r => selectedPaths.Contains(r.RelativePath ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .ToList();

            IsBusy = true;
            _scheduler.Run<IgnoreOutcome>(
                () =>
                {
                    if (confirmed)
                    {
                        var result = _gitManager.DiscardChanges(server, database, selectedPaths);

                        // 되돌리지 못한 파일의 행은 닫지 않는다. 성공한 것까지 막지는 않는다 -
                        // 전부 막으면 한 파일이 잠긴 것 때문에 나머지가 다음 새로고침에 되살아난다.
                        var closable = records
                            .Where(r => !result.FailedPaths.Contains(r.RelativePath ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                            .ToList();

                        return new IgnoreOutcome
                        {
                            Result = result,
                            ClosedRows = closable.Count(r => r.LastLogId > 0),
                            MarkProcessedFailure = _stateTracker.MarkProcessed(server, database, closable)
                        };
                    }

                    var states = _gitManager.GetChangedFileStates(gitPath);
                    return new IgnoreOutcome
                    {
                        Plan = DiscardPlan.Build(selectedPaths, states),
                        ClosedRows = records.Count(r => r.LastLogId > 0),
                        // 남이 만졌다는 판정은 커밋이 쓰는 그것을 그대로 쓴다. 뷰모델은 "내가 누구인지"를
                        // 모르고(그 값은 서버가 안다), 새 API를 만들면 판정이 두 곳으로 갈라진다.
                        ForeignAuthors = (_stateTracker.GetCoAuthorWarnings(
                                    server, database, records.Select(r => r.QualifiedName))
                                ?? Array.Empty<CoAuthorWarning>())
                            .Select(w => w.Author)
                            .Where(a => !string.IsNullOrWhiteSpace(a))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    };
                },
                outcome =>
                {
                    IsBusy = false;

                    if (outcome.Plan != null)
                    {
                        // 되돌릴 것이 없어도 닫을 행이 있으면 무시는 할 일이 있다.
                        if (outcome.Plan.IsEmpty && outcome.ClosedRows == 0)
                        {
                            WarningMessage = "무시할 대상이 없습니다.";
                            return;
                        }

                        if (_notifier.Confirm(
                                "DBVC 무시 확인",
                                BuildIgnoreConfirmation(outcome.Plan, outcome.ClosedRows, outcome.ForeignAuthors)))
                        {
                            Ignore(confirmed: true);
                        }

                        return;
                    }

                    var result = outcome.Result!;

                    if (result.HasFailures)
                    {
                        _notifier.ShowError(
                            "DBVC 무시 — 일부 실패",
                            "다음 파일을 되돌리지 못해 그 항목의 변경 로그도 닫지 않았습니다."
                            + " 다른 프로그램이 파일을 열고 있는지 확인하세요."
                            + Environment.NewLine + Environment.NewLine
                            + string.Join(Environment.NewLine, result.FailedPaths.Select(p => "  · " + p)));
                    }

                    // 커밋과 같은 자리의 실패다. 삼키면 그 항목이 새로고침마다 되살아난다.
                    if (outcome.MarkProcessedFailure != null)
                    {
                        _notifier.ShowError("DBVC 무시 — 변경 로그를 닫지 못함", outcome.MarkProcessedFailure);
                    }

                    _pendingStatusMessage = BuildIgnoreSummary(result, outcome.ClosedRows);

                    // 재추출하지 않는다. 하면 아직 닫지 못한 행이 가리키는 객체가 다시 추출되어
                    // 같은 클릭 안에서 되돌리기가 취소된다(되돌리기와 같은 이유).
                    Refresh(fullExtraction: false, reloadHistory: false, syncRepository: false);
                },
                ex =>
                {
                    IsBusy = false;
                    _notifier.ShowError("DBVC 무시 실패", ex.Message);
                });
        }

        private sealed class IgnoreOutcome
        {
            public DiscardPlan? Plan { get; set; }
            public DiscardResult? Result { get; set; }
            public int ClosedRows { get; set; }
            public IReadOnlyList<string> ForeignAuthors { get; set; } = new List<string>();
            public string? MarkProcessedFailure { get; set; }
        }
```

`GetCoAuthorWarnings`를 쓰는 이유는 뷰모델이 "내가 누구인지"를 모르기 때문이다 — 그 값은 서버가 `SUSER_SNAME()`/`HOST_NAME()`으로 답하고, `StateTracker`만 읽는다. 커밋이 같은 자리에서 같은 API를 이미 부르므로(`:1792`) 판정이 한 곳에 남는다. 이 호출은 백그라운드 람다 안에 있어야 한다 — DB를 읽는다.

`:2234`의 `RaiseCanExecuteChanged` 구역에 더한다:

```csharp
            (IgnoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

`src/DBVC.Vsix/UI/ViewChangesControl.xaml:191`의 되돌리기 버튼 **뒤**에 더한다:

```xml
                    <Button Content="무시" Command="{Binding IgnoreCommand}" Width="70" Margin="0,0,10,4"
                            ToolTip="선택한 파일을 되돌리고 변경 로그에서 닫습니다. 이 데이터베이스를 함께 쓰는 모두에게 적용됩니다." />
```

되돌리기 버튼의 ToolTip도 차이가 읽히도록 손본다:

```xml
                            ToolTip="선택한 파일을 저장소의 마지막 커밋 내용으로 되돌립니다. 데이터베이스의 변경은 남아 다음 새로고침에서 다시 추출됩니다."
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests`
Expected: PASS. 버튼이 하나 늘었으므로 `TopRowLayoutTests`가 버튼 수나 순서를 단언한다면 그 기대값도 함께 고친다.

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 파일을 되돌리고 로그 행을 닫는 무시를 더한다"
```

---

### Task 9: SQL Server 통합 테스트

SQL Server가 없으면 Skip이다. **Skip은 통과가 아니다** — 이 태스크의 완료는 실제 서버에서 초록을 본 뒤에만 주장한다.

**Files:**
- Test: `tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs`

**Interfaces:**
- Consumes: `dbo.DBVC_PurgeChangeLog` (Task 4), 트리거의 접두사 제외 (Task 3)
- Produces: 없음

- [ ] **Step 1: 테스트를 쓴다**

이 픽스처의 기존 헬퍼(`_db.Execute`, `_db.QueryScalar`, `_db.ExecuteInOneSession`)를 그대로 쓴다. `[OneTimeSetUp]`이 실제 설치 경로로 v6를 이미 설치해 둔다.

```csharp
        [Test]
        public void Purge_DeletesUnprocessedRows_WhenOlderThanRetention()
        {
            // 이 규칙이 이 설계의 핵심이다. IsProcessed를 보면 미채택자의 행에 영영 닿지 못한다.
            _db!.Execute(
                "INSERT INTO dbo.DBVC_ChangeLog (EventType, SchemaName, ObjectName, ObjectType, PostTime, LoginName, IsProcessed) " +
                "VALUES (N'ALTER_TABLE', N'dbo', N'PurgeOldOpen', N'TABLE', DATEADD(day, -400, GETDATE()), N'nobody', 0)");

            _db.Execute("EXEC dbo.DBVC_PurgeChangeLog");

            var left = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'PurgeOldOpen'"));
            Assert.That(left, Is.Zero);
        }

        [Test]
        public void Purge_KeepsRows_WhenWithinRetention()
        {
            _db!.Execute(
                "INSERT INTO dbo.DBVC_ChangeLog (EventType, SchemaName, ObjectName, ObjectType, PostTime, LoginName, IsProcessed) " +
                "VALUES (N'ALTER_TABLE', N'dbo', N'PurgeRecent', N'TABLE', DATEADD(day, -1, GETDATE()), N'nobody', 0)");

            _db.Execute("EXEC dbo.DBVC_PurgeChangeLog");

            var left = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'PurgeRecent'"));
            Assert.That(left, Is.EqualTo(1));
        }

        [Test]
        public void Purge_DeletesAllRows_WhenCountExceedsBatchSize()
        {
            // 배치 루프가 한 번만 돌고 멈추면 5000개만 지워진다. 5001개로 그것을 태운다.
            _db!.Execute(
                "INSERT INTO dbo.DBVC_ChangeLog (EventType, SchemaName, ObjectName, ObjectType, PostTime, LoginName, IsProcessed) " +
                "SELECT TOP (5001) N'ALTER_TABLE', N'dbo', N'PurgeBulk', N'TABLE', DATEADD(day, -400, GETDATE()), N'nobody', 0 " +
                "FROM sys.all_columns a CROSS JOIN sys.all_columns b");

            _db.Execute("EXEC dbo.DBVC_PurgeChangeLog");

            var left = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'PurgeBulk'"));
            Assert.That(left, Is.Zero);
        }

        [Test]
        public void Purge_Succeeds_WhenCallerIsNotOwner()
        {
            // public에 DELETE를 주지 않았으므로 EXECUTE AS OWNER가 아니면 여기서 죽는다.
            // 공용 계정이 db_owner가 아닌 환경이 실제로 그렇다.
            _db!.Execute(
                "INSERT INTO dbo.DBVC_ChangeLog (EventType, SchemaName, ObjectName, ObjectType, PostTime, LoginName, IsProcessed) " +
                "VALUES (N'ALTER_TABLE', N'dbo', N'PurgeLowPriv', N'TABLE', DATEADD(day, -400, GETDATE()), N'nobody', 0)");

            // 저권한 사용자 만들기·되돌리기는 이 파일의
            // Trigger_LogsTheChange_WhenAnUnprivilegedUserRunsDdl이 쓰는 관용을 그대로 따른다.
            _db.ExecuteInOneSession(
                "CREATE USER LowPrivPurge WITHOUT LOGIN;",
                "EXECUTE AS USER = N'LowPrivPurge';",
                "EXEC dbo.DBVC_PurgeChangeLog;",
                "REVERT;",
                "DROP USER LowPrivPurge;");

            var left = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'PurgeLowPriv'"));
            Assert.That(left, Is.Zero);
        }

        [Test]
        public void Trigger_DoesNotLog_WhenDbvcOwnedObjectIsCreated()
        {
            // 설치가 자기 자신을 사용자 변경으로 기록하면 그것이 저장소에 커밋된다.
            var before = Convert.ToInt32(_db!.QueryScalar("SELECT COUNT(*) FROM dbo.DBVC_ChangeLog"));

            _db.Execute("CREATE TABLE dbo.DBVC_ScratchTable (Id INT NULL);");
            _db.Execute("DROP TABLE dbo.DBVC_ScratchTable;");

            var after = Convert.ToInt32(_db.QueryScalar("SELECT COUNT(*) FROM dbo.DBVC_ChangeLog"));
            Assert.That(after, Is.EqualTo(before));
        }

        [Test]
        public void Trigger_Logs_WhenNameOnlyResemblesTheDbvcPrefix()
        {
            // LIKE의 [_] 이스케이프가 빠지면 이 테이블이 조용히 추적에서 빠진다.
            _db!.Execute("CREATE TABLE dbo.DBVCxResemble (Id INT NULL);");

            var logged = Convert.ToInt32(_db.QueryScalar(
                "SELECT COUNT(*) FROM dbo.DBVC_ChangeLog WHERE ObjectName = N'DBVCxResemble'"));

            _db.Execute("DROP TABLE dbo.DBVCxResemble;");
            Assert.That(logged, Is.GreaterThan(0));
        }
```

`SqlServerTestDatabase`의 헬퍼는 `Execute(string)`, `ExecuteInOneSession(params string[])`, `QueryScalar(string)`이다 — 위 코드가 쓰는 그대로다.

- [ ] **Step 2: 실행한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DdlTriggerIntegrationTests"`
Expected: SQL Server가 있으면 PASS. 없으면 Skip — 그 경우 **이 태스크를 완료로 표시하지 말고 그 사실을 보고한다.**

- [ ] **Step 3: 커밋**

```bash
git add tests/DBVC.Core.Tests/DdlTriggerIntegrationTests.cs
git commit -m "test(core): 정리 프로시저와 접두사 제외를 실제 서버에서 검증한다"
```

---

### Task 10: 문서·버전·백로그

**Files:**
- Modify: `README.md`
- Modify: `docs/setup-checklist.md`
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest`
- Modify: `docs/team-rollout-backlog.md`

**Interfaces:**
- Consumes: 앞의 모든 태스크
- Produces: 없음

- [ ] **Step 1: `README.md`를 고친다**

세 곳을 더한다.

1. 커밋·되돌리기를 설명하는 구역에 **무시** 문단:

```markdown
- **무시:** 항목을 체크하고 **무시** 를 누르면 파일이 마지막 커밋 내용으로 되돌아가고
  `DBVC_ChangeLog`의 해당 행이 닫힙니다. **되돌리기와 달리 이 데이터베이스를 함께 쓰는
  모두에게 적용됩니다** — 닫힌 변경은 다시 나타나지 않고 git에도 담기지 않습니다.
  데이터베이스의 변경 자체는 취소되지 않습니다.
```

2. 변경 추적 구역에 보존 정책:

```markdown
- **변경 로그 보존:** `DBVC_ChangeLog`는 30일이 지난 행을 처리 여부와 관계없이 지웁니다.
  새로고침할 때 정리가 함께 돕니다. 30일이 지나도록 아무도 커밋하지 않은 변경은 그 뒤
  "주인 없는 변경"으로 목록에 나타나므로, 누구든 커밋하거나 되돌릴 수 있습니다.
```

3. `DBVC_` 접두사 선언(권한·설치를 설명하는 `:91` 부근):

```markdown
  `DBVC_`로 시작하는 이름과 `trg_DBVC_DDL_Tracker`는 DBVC가 쓰는 이름공간입니다. 이 이름의
  객체는 변경 추적에도 추출에도 잡히지 않으므로, 사용자 객체에 이 접두사를 쓰지 마세요.
```

- [ ] **Step 2: `docs/setup-checklist.md`를 고친다**

스키마 버전을 언급하는 자리를 v6로 올리고, "변경 추적기 업데이트"를 눌러야 정리 프로시저가 설치된다는 것을 적는다.

- [ ] **Step 3: 확장 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest`의 `Version`을 `0.5.21`로 올린다(현재 값이 `0.5.20`인지 먼저 확인한다).

- [ ] **Step 4: 백로그를 닫는다**

`docs/team-rollout-backlog.md`에서:
- "지금 할 일의 순서" 표의 `**P1** | 4번 + 5번 + "무시"` 행을 `~~P1~~` 취소선으로 바꾸고 사유에 이 스펙을 건다
- 4번·5번 항목 본문에 무엇으로 닫혔는지 적는다
- 2번 항목의 "그 동작('무시')은 아래 4·5번과 한 몸으로 설계한다"를 완료로 바꾼다
- 머리말에 0.5.21로 닫혔다는 한 줄을 더한다

- [ ] **Step 5: 전체 빌드와 테스트**

```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests
```
Expected: 모두 PASS

- [ ] **Step 6: 커밋**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest docs/team-rollout-backlog.md
git commit -m "docs: 무시와 30일 보존 정책을 문서에 반영하고 백로그 4·5번을 닫는다"
```

---

## 완료 조건

`dotnet test`가 초록이어도 **끝이 아니다.** CLAUDE.md가 못 박은 대로, 아래를 SSMS 21에서 직접 눌러 보기 전에는 "동작한다"고 말하지 않는다.

- [ ] 무시 버튼이 되돌리기 옆에 뜬다
- [ ] 배포·감사 클론에 연결하면 무시 버튼이 없다(목록 자체가 없다)
- [ ] 확인 대화상자에 "이 데이터베이스를 함께 쓰는 모두에게 적용됩니다"가 보인다
- [ ] 무시 → 항목이 사라지고, 새로고침해도 돌아오지 않는다
- [ ] 구버전(v5) DB에 연결하면 "변경 추적기 업데이트 필요"가 뜨고, 누르면 v6로 올라가며 `dbo.DBVC_PurgeChangeLog`가 생긴다
- [ ] "전체 다시 추출" 뒤 저장소에 `DBVC_PurgeChangeLog.sql`이 **없다**

SQL Server 없이 개발했다면 Task 9가 Skip으로 지나간다. 그 경우 **위 마지막 두 항목이 유일한 검증**이므로 반드시 밟는다.
