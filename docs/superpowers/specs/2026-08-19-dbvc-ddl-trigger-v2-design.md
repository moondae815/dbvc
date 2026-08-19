# DDL 트리거 계약 v2 설계 — 권한·인덱스·유령 이벤트

## 1. 문제

`InstallTrigger.sql`이 설치하는 DDL 트리거에 결함이 셋 있다. 셋 다 로컬 SQL Server 2022에
임시 데이터베이스를 만들어 재현했다.

### 1.1 권한 없는 사용자의 DDL이 **실패한다**

트리거의 `BEGIN CATCH`에는 "Suppress trigger errors so database operations do not fail if
logging fails"라고 적혀 있다. 그렇게 동작하지 않는다. 트리거 안에서 오류가 나면 트랜잭션이
uncommittable(`XACT_STATE() = -1`) 상태가 되고, CATCH가 삼켜도 SQL Server가 오류 3616으로
배치를 중단하고 롤백한다.

```
-- CREATE TABLE 권한은 있고 DBVC_ChangeLog에 INSERT 권한이 없는 사용자
EXECUTE AS USER = 'dbvc_low';
CREATE TABLE dbo.LowPrivTable (Id int);
→ 메시지 3616: 트리거를 실행하는 동안 오류가 발생했습니다. 배치 처리가 중단되고 롤백됩니다.
```

즉 **공유 개발 DB에 DBVC를 설치하면 db_owner가 아닌 팀원은 그 순간부터 객체를 만들 수 없다.**
DBVC를 쓰지 않는 사람까지 막는다는 점에서 이 스펙에서 가장 무거운 결함이다.

### 1.2 인덱스를 만들거나 지워도 부모 테이블이 다시 추출되지 않는다

0.2.4에서 인덱스를 테이블 스크립트에 담기로 했는데(`2026-08-19-dbvc-table-dri-and-indexes-design.md`)
변경 감지가 따라가지 못한다. 트리거가 기록한 실제 로그다.

| EventType | ObjectName | ObjectType |
|---|---|---|
| CREATE_TABLE | Users | TABLE |
| CREATE_INDEX | IX_Users_Name | INDEX |
| ALTER_TABLE | Users | TABLE |
| DROP_INDEX | IX_Users_Name | INDEX |
| CREATE_EXTENDED_PROPERTY | Users | TABLE |
| CREATE_TRIGGER | trg_Users_Audit | TRIGGER |

EVENTDATA의 `TargetObjectName`(= 부모 테이블)을 기록하지 않으므로, 새로고침은
`dbo.IX_Users_Name`만 추출 대상으로 잡고 테이블은 건드리지 않는다. 저장소에는 지워진 인덱스가
남고 새 인덱스는 들어오지 않는다 — **저장소가 데이터베이스와 어긋나는데 화면은 아무 말도 하지
않는다.** 확장 속성은 부모 테이블로 기록되므로(위 표 5행) 이 문제에 해당하지 않는다.

### 1.3 스크립팅할 수 없는 이벤트가 목록을 오염시킨다

`DDL_DATABASE_LEVEL_EVENTS`는 사용자·역할·권한까지 모두 포함한다.

| EventType | ObjectName |
|---|---|
| CREATE_USER | dbvc_low |
| GRANT_DATABASE | DBVC_ReviewTmp2 |

이 행들은 `ObjectPathConvention`에서 `Other` 폴더로 떨어져 `dbo/Other/dbvc_low.sql` 같은
경로를 얻는다. 파일은 영원히 만들어지지 않으므로 화면에는 대응하는 파일이 없는 항목이 뜬다.
`Commands.Stage`가 없는 경로를 예외 없이 넘기는 것은 확인했으므로 커밋을 깨뜨리지는 않지만,
그 항목만 체크하고 커밋하면 "커밋할 변경사항이 없습니다"만 나오고 목록에 계속 남는다.

### 1.4 함께 드러난 두 가지

- **`SET QUOTED_IDENTIFIER ON`이 없다.** 트리거는 이 옵션을 생성 시점 값으로 저장하고, 본문의
  `EVENTDATA().value()`는 그것이 ON이어야 동작한다. QI가 기본 OFF인 클라이언트(sqlcmd)로
  설치했더니 그 데이터베이스의 **모든 DDL이 오류 1934 → 3616으로 실패했다.** DBVC의 정상
  경로(`Microsoft.Data.SqlClient`)는 QI를 ON으로 보내므로 지금까지 드러나지 않았을 뿐,
  스크립트가 클라이언트 기본값에 기대고 있을 이유가 없다.
- **이미 설치된 트리거를 갈아 끼울 경로가 없다.** `ViewChangesControl.xaml`의 "DBVC 초기화"
  오버레이는 `IsInitialized`가 false일 때만 뜨고, `StateTracker.IsInitializedQuery`는 테이블과
  트리거의 *존재*만 본다. 버전 개념이 없으므로 트리거를 고쳐도 기존 사용자에게 닿지 않는다.

## 2. 결정

트리거의 계약을 v2로 올린다. 판단은 두 곳으로 나눈다 — **기록할지 말지는 트리거가, 기록된 것을
무엇으로 볼지는 Core가** 정한다. 오늘 찾은 결함 중 둘(1.1, 1.4)이 어떤 테스트도 닿지 않는
SQL 안에 있었으므로, 새 규칙을 SQL에 쌓지 않는 것이 이 결정의 핵심이다.

| 문제 | 해결 | 어디서 |
|---|---|---|
| 1.1 권한 | `WITH EXECUTE AS 'dbo'`, `CATCH` 삭제 | 트리거 |
| 1.2 인덱스 | `TargetObjectName`·`TargetObjectType` 기록 → 부모로 정규화 | 트리거(기록) + Core(해석) |
| 1.3 유령 이벤트 | 추적 타입 화이트리스트 | 트리거 |
| 1.4 QI | 스크립트 첫머리에 `SET` 두 줄 | 트리거 |
| 1.4 재설치 | 스키마 버전 + 배너의 "추적기 업데이트" | 트리거(표식) + Vsix(UI) |

**UTF-16 인코딩 문제는 이 스펙 밖이다.** 저장소에 쌓인 파일을 다시 쓰는 작업이라 위험·검증·
롤백이 전혀 다르고, 이 스펙이 요구하는 트리거 재설치와 한 릴리스에 겹치면 어느 쪽이 무엇을
깨뜨렸는지 가릴 수 없다. 별도 스펙으로 뒤에 낸다.

## 3. 설계

### 3.1 트리거를 dbo 권한으로 실행한다

```sql
CREATE TRIGGER [trg_DBVC_DDL_Tracker]
ON DATABASE
WITH EXECUTE AS 'dbo'
FOR DDL_DATABASE_LEVEL_EVENTS
```

로깅 INSERT가 dbo 권한으로 돌아 사용자에게 권한을 더 주지 않아도 된다. `GRANT INSERT ... TO
public`도 같은 문제를 풀지만, 그 경우 모든 DB 사용자가 변경 로그에 직접 행을 넣을 수 있게 되어
로그를 근거로 삼는 이 도구의 전제가 약해진다.

`LoginName`은 `EVENTDATA()`에서 오므로 가장(impersonation)과 무관하게 실제 실행자가 남는다.
다만 세션 자체가 `EXECUTE AS USER`로 가장된 상태면 로그인이 없어 SID가 기록된다 — SQL Server의
동작이며 DBVC가 관여할 수 있는 부분이 아니다.

**설치 요구사항이 하나 늘어난다.** `EXECUTE AS 'dbo'`로 트리거를 만들려면 설치자가 dbo를 가장할
수 있어야 한다(db_owner면 충족). 소유자 SID가 유효하지 않은 데이터베이스에서는 실패하며, 그때는
`InitializeDatabase`가 예외를 그대로 전파해 화면이 사유를 보여준다 — 지금과 같은 경로다.

**`BEGIN CATCH`는 지운다.** 트리거 안의 오류를 무해하게 만드는 방법은 없다. 삼키는 척하는 코드는
실패를 감추지도 못하면서 "여기는 안전하다"는 잘못된 믿음만 남긴다.

### 3.2 기록할 것만 기록한다

트리거는 DBVC가 실제로 스크립팅하는 타입과, 부모로 정규화될 `INDEX`만 기록한다.

```sql
-- DBVC_TRACKED_TYPES: 이 목록은 ObjectPathConvention과 테스트로 동기화된다. 형식을 바꾸지 말 것.
IF @ObjectType NOT IN (N'TABLE', N'VIEW', N'PROCEDURE', N'SQL_STORED_PROCEDURE',
    N'FUNCTION', N'SQL_SCALAR_FUNCTION', N'SQL_TABLE_VALUED_FUNCTION',
    N'SQL_INLINE_TABLE_VALUED_FUNCTION', N'TRIGGER', N'SQL_TRIGGER', N'TYPE',
    N'TABLE_TYPE', N'SEQUENCE OBJECT', N'SEQUENCE_OBJECT', N'SEQUENCE', N'SYNONYM',
    N'INDEX')
    RETURN;
```

목록이 두 곳(SQL과 `ObjectPathConvention`)에 생기므로 어긋남을 테스트로 막는다. 지금
`FolderByObjectType`은 SMO 타입명과 DDL 이벤트 타입명을 한 사전에 섞어 두고 있어 어느 쪽이
DDL용인지 코드가 말해 주지 않는다. **DDL 이벤트 타입 집합을 이름 있는 자리로 빼고
(`ObjectPathConvention.DdlEventObjectTypes`) 폴더 사전을 그 집합에서 만든다.** 설치 스크립트는
임베디드 리소스이고 `ReadInstallScript()`가 이미 `internal`이므로, 테스트가 위 표식 주석 아래
목록을 읽어 그 집합과 비교할 수 있다.

### 3.3 부모를 가리키는 근거를 남긴다

`DBVC_ChangeLog`에 컬럼 둘을 더한다. 기존 스크립트의 멱등 `ALTER TABLE ... ADD` 패턴을 그대로
따른다.

```sql
[TargetObjectName] NVARCHAR(256) NULL,
[TargetObjectType] NVARCHAR(100) NULL
```

값은 `EVENTDATA()`의 `TargetObjectName`/`TargetObjectType`을 그대로 넣는다. 트리거는 사실만
남기고 해석하지 않는다 — 인덱스 이벤트의 `ObjectName`은 여전히 인덱스 이름이므로, 로그는
사용자가 실제로 실행한 DDL과 어긋나지 않는다.

### 3.4 스키마 버전 표식

`DBVC_ChangeLog`의 확장 속성으로 둔다. 객체가 늘지 않고, 이 확장 속성을 다는 DDL 자체는
트리거의 `DBVC_ChangeLog` 예외 규칙에 걸려 로그를 더럽히지 않는다.

```sql
-- 설치: 없으면 sp_addextendedproperty, 있으면 sp_updateextendedproperty (멱등)
EXEC sp_addextendedproperty @name = N'DBVC_SchemaVersion', @value = N'2',
     @level0type = N'SCHEMA', @level0name = N'dbo',
     @level1type = N'TABLE',  @level1name = N'DBVC_ChangeLog';
```

읽기는 한 번의 왕복으로 끝낸다. 테이블과 트리거가 모두 없으면 `0`, 있는데 표식이 없으면 `1`,
있으면 그 값이다.

```sql
SELECT CASE
    WHEN NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]') AND type = N'U')
      OR NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = N'trg_DBVC_DDL_Tracker')
    THEN 0
    ELSE ISNULL((SELECT TRY_CAST(CAST(value AS NVARCHAR(50)) AS int) FROM sys.extended_properties
                 WHERE class = 1 AND major_id = OBJECT_ID(N'[dbo].[DBVC_ChangeLog]')
                   AND minor_id = 0 AND name = N'DBVC_SchemaVersion'), 1)
END
```

C# 쪽 상수는 `StateTracker.RequiredSchemaVersion`이며, 3.2의 동기화 테스트가 스크립트의 값과
이 상수가 같은지도 함께 본다.

### 3.5 Core: 입구에서 한 번 정규화한다

`ReadPendingRows`가 새 컬럼까지 읽고, **읽자마자 정규화한다.** `GetChangedObjectNames`(추출
대상)와 `BuildChangeSet`(화면 목록)이 각자 해석하면 두 경로가 갈라진다 — 추출은 테이블을 다시
뽑았는데 목록은 인덱스를 보여주는 식이 된다.

규칙은 하나다. **`ObjectType`이 `INDEX`이고 `TargetObjectName`이 있으면 이름·타입을 `Target*`으로
치환한다.** `TargetObjectName`이 비어 있으면(v1이 남긴 행) 손대지 않는다.

여기 함정이 있다. `DROP_INDEX`는 `MapEventTypeToState`에서 `"Deleted"`가 되고, `ResolveState`는
Deleted면 Git 상태를 무시하고 그대로 통과시키며, `WorkingTreeCleaner`는 그것을 보고 파일을
지운다. 이름만 바꾸고 이벤트를 그대로 두면 **인덱스를 지웠을 뿐인데 테이블의 `.sql`이 저장소에서
삭제된다.** 그래서 정규화는 이벤트 타입도 함께 옮긴다.

| 원래 EventType | 정규화 후 |
|---|---|
| `CREATE_INDEX` / `ALTER_INDEX` / `DROP_INDEX` | `ALTER_TABLE` |

인덱스 변경은 부모 테이블의 **수정**이지 삭제가 아니다.

`ObjectPathConvention`에 `INDEX` 폴더를 더하지 않는다. 인덱스는 독립 객체로 저장되지 않으므로
정규화되지 못한 행(v1 잔여)이 `Other`로 떨어지는 것이 오히려 정확하다.

### 3.6 Core: 처리 완료 표시를 넓힌다

`MarkProcessedCommand`는 지금 `ObjectName = @objectName`으로 행을 닫는다. 정규화하면 레코드의
이름은 테이블이고 로그의 행은 인덱스 이름이므로, **커밋해도 인덱스 행이 닫히지 않고 다음
새로고침에 다시 올라온다.** 조건을 넓힌다.

```sql
WHERE IsProcessed = 0 AND Id <= @lastLogId
  AND (ObjectName = @objectName OR TargetObjectName = @objectName)
  AND (ISNULL(SchemaName, N'dbo') = @schemaName)
```

테이블 하나를 커밋하면 그 테이블의 `ALTER_TABLE` 행과 딸린 인덱스 행이 함께 닫힌다.
`Id <= @lastLogId` 조건은 그대로 두어 새로고침 이후에 들어온 이벤트는 건드리지 않는다.

### 3.7 Vsix: 구버전을 알리고 갈아 끼운다

`IStateTracker.IsInitialized(bool)`를 `int GetInstalledVersion(server, database)`으로 대체한다.
호출부는 `ProbeContext` 하나뿐이라 접속 횟수는 늘지 않는다. ViewModel의 `IsInitialized` 속성과
그 XAML 바인딩은 그대로 둔다 — 바뀌는 것은 Core 인터페이스이고, 화면은 여전히 "초기화되었는가"를
묻는다. 오버레이는 `version == 0`, 새 안내는 `0 < version < RequiredSchemaVersion`으로 갈린다.

안내는 **기존 경고 배너에 얹지 않고 그 아래 별도 한 줄**에 둔다. `WarningMessage`는 접속 실패·
매핑 없음 등으로 이미 쓰이고 있어 겹치면 한쪽이 다른 쪽을 지운다.

> 변경 추적기가 구버전입니다. 인덱스 변경이 저장소에 반영되지 않습니다.  [추적기 업데이트]

버튼은 `InitializeDatabase`와 같은 멱등 스크립트를 다시 실행한다. **이때 `Setup()`도 함께
백그라운드로 옮긴다.** 업데이트와 초기화가 같은 코드 경로인데 한쪽만 UI 스레드에 남기면 같은
결함을 두 벌 갖게 된다. 둘 다 `IBackgroundScheduler`로 내보내고 `IsBusy`를 세워 실행 중 다른
버튼을 잠근다.

### 3.8 기존 로그 마이그레이션

v2 스크립트가 설치 끝에 한 번, 커밋될 수 없는 미처리 행을 닫는다.

```sql
UPDATE dbo.DBVC_ChangeLog SET IsProcessed = 1
WHERE IsProcessed = 0
  AND (ObjectType NOT IN (/* 3.2의 DBVC_TRACKED_TYPES 목록을 그대로 쓴다 */)
       OR (ObjectType = N'INDEX' AND TargetObjectName IS NULL));
```

(a) 화이트리스트 밖 타입은 파일이 생길 수 없고, (b) 부모를 모르는 v1 인덱스 행은 정규화할 수
없다. 그대로 두면 목록에 영원히 남는다. v2 이후로는 트리거가 그런 행을 만들지 않으므로 이
정리는 옛 행에만 닿고, 여러 번 실행해도 결과가 같다.

(b) 때문에 과거의 인덱스 변경이 조용히 사라진다. 그래서 업데이트 완료 알림에 **"전체 다시 추출을
한 번 눌러 주세요"**를 넣는다 — 저장소를 데이터베이스의 현재 상태와 맞추는 확실한 경로가 그것뿐이다.

## 4. 검증

**Core 단위 테스트** (`Method_Result_WhenCondition`)

- 정규화: `INDEX` + `Target` 있음 → 부모 이름·타입으로 치환된다
- 정규화: `DROP_INDEX` → 상태가 `Deleted`가 아니라 `Modified`가 된다 (**이 테스트를 먼저 쓴다**)
- 정규화: `TargetObjectName`이 없으면 행을 바꾸지 않는다
- 정규화: 인덱스 이벤트가 추출 대상 이름(`GetChangedObjectNames`)에도 부모로 나온다
- `MarkProcessed`: 테이블을 커밋하면 그 테이블의 인덱스 행도 닫힌다
- `GetInstalledVersion`: 0 / 1 / 2 세 상태를 가른다
- ViewModel: `0 < version < Required`일 때만 업데이트 안내와 명령이 활성화된다
- ViewModel: 초기화·업데이트가 `IBackgroundScheduler`를 거치고 실행 중 `IsBusy`가 선다

**동기화 테스트** — 설치 스크립트 텍스트에서 `DBVC_TRACKED_TYPES` 목록과 버전 값을 읽어
`ObjectPathConvention.DdlEventObjectTypes` 및 `StateTracker.RequiredSchemaVersion`과 비교한다.
한쪽만 고치면 죽어야 한다.

**트리거 통합 테스트** — 새 픽스처를 만든다. 트리거 SQL에는 지금까지 어떤 테스트도 닿지 않았고
오늘 결함 둘이 거기서 나왔다. `SmoManagerIntegrationTests`와 같은 방침으로 로컬 SQL Server에
접속되지 않으면 Skip한다.

- 저권한 사용자(`CREATE TABLE`만 가진)의 DDL이 성공하고 로그에 남는다 — 1.1의 회귀 테스트
- `CREATE INDEX`가 `TargetObjectName`에 부모 테이블을 남긴다
- `GRANT`·`CREATE USER`는 기록되지 않는다
- 스크립트를 두 번 실행해도 결과가 같다(멱등)
- v1 상태(구 컬럼·구 트리거·유령 행)에서 실행하면 v2가 되고 커밋 불가 행이 닫힌다

**테스트 데이터베이스 누수** — 통합 픽스처가 늘어나는 김에 함께 잡는다. 지금 localhost에
`DBVC_ITest_*`가 6개 쌓여 있다. 공통 베이스의 `OneTimeSetUp`이 시작할 때 이름 규칙에 맞고
**생성된 지 한 시간이 지난** 데이터베이스만 지운다 — 시각 조건이 없으면 같은 서버에서 동시에
도는 다른 실행의 것을 지운다.

**CI가 검증하지 못하는 것** — SSMS 21에서 직접 확인한다. 구버전 트리거를 가진 DB를 열어 안내
배너와 버튼이 뜨는지, 업데이트 후 인덱스를 만들었을 때 테이블이 다시 추출되고 비교창에 인덱스가
들어오는지, 업데이트 중에 쿼리 편집기가 그대로 쓰이는지.

## 5. 범위 밖

- **UTF-16 → UTF-8 전환** — 별도 스펙. 이유는 2절에 있다.
- `MarkProcessed`·`ScriptObjectsDetailed`의 실패 침묵, 매핑 변경 UI, 새로고침의 중복 조회 —
  같은 검토에서 나왔지만 이 스펙의 세 결함과 원인이 다르다.
- Object Explorer 오버레이(Feature 10)는 여전히 보류다.

## 6. 릴리스

- `source.extension.vsixmanifest`를 0.2.6으로 올린다.
- `README.md` — 인덱스 변경이 추적된다는 사실, 구버전 안내 배너, 업데이트 후 전체 다시 추출 권장.
- `docs/setup-checklist.md` — 설치자에게 dbo 가장 권한(db_owner)이 필요하다는 요구사항,
  DBVC를 쓰지 않는 팀원의 DDL이 막히지 않는다는 점.
