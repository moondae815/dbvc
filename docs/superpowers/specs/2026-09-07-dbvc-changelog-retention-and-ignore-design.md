# 변경 로그 보존과 "무시" 설계 — 열린 채 영원히 남는 행을 닫는다

`docs/team-rollout-backlog.md`의 4번(ChangeLog 보존 정책)·5번(미채택자 행 누적)과, 되돌리기
설계([2026-09-07-dbvc-discard-changes-design.md](2026-09-07-dbvc-discard-changes-design.md))
2.1이 여기로 미뤄 둔 "무시"를 한 설계로 닫는다. 백로그가 셋을 한 덩어리로 묶은 판단이 옳았다 —
아래 1.5가 그 이유다.

## 1. 문제

### 1.1 닫힌 행은 아무도 읽지 않는데 영원히 쌓인다

`DBVC_ChangeLog`에 정리가 없다. 커밋으로 `IsProcessed = 1`이 된 행은 도구의 어느 경로도 다시
읽지 않는다 — `ReadPendingRows`도 `GetCoAuthorWarnings`도 `WHERE IsProcessed = 0`이다. 그런데
행은 남는다. 공용 개발 DB 하나를 23명이 쓰므로 증가는 사람 수에 비례한다.

덩치의 대부분은 `TSQLCommand NVARCHAR(MAX)`다. 테이블 디자이너 한 번이 CREATE 뒤로 ALTER를
여러 개 흘리므로 행 수도 사람의 체감보다 빠르게 는다.

### 1.2 미채택자의 행은 절대 닫히지 않는다

DBVC를 쓰지 않는 사람의 DDL도 트리거는 기록한다. 트리거는 DATABASE 범위라 그 사람이 도구를
설치했는지와 무관하다. 그 행을 닫는 길은 커밋뿐인데 그 사람은 커밋하지 않는다. 따라서
`IsProcessed = 0`으로 영구히 남는다.

**이것이 1.1을 되살린다.** "닫힌 행을 지운다"는 정책은 미채택자 행에 **정의상 닿지 않는다.**
4번만 그렇게 풀면 표는 계속 커지고, 커지는 부분이 하필 매 조회가 읽는 열린 행이다.

### 1.3 진짜 피해는 목록이 아니라 작업 트리에 쌓인다

백로그는 5번을 "보이는 것보다 덜 급하다 — '내 변경만' 필터가 남의 행을 이미 가린다"로 적었다.
**그 근거는 절반만 맞다.**

`GetChangedObjectNames`는 작업자로 좁히지 않는다(`StateTracker.cs:360`의 주석에 그 이유가 있다 —
남의 변경을 추출에서 빼면 그 객체의 파일이 낡은 채로 남아 다음 커밋에 섞여 들어간다).
`ViewChangesViewModel.Extract`(`:1612`)가 그 목록을 그대로 SMO에 넘긴다. 그래서 미채택자가
`dbo.Foo`를 한 번 고치면:

1. **23명 모두의** 새로고침이 `dbo.Foo`를 추출해 `.sql`을 덮어쓴다 — 각자의 작업 트리가 더러워진다
2. `PartitionByAuthor`가 그 경로를 `foreignPaths`로 넘기고 `BuildChangeSet`의 Git 폴백이 그것을
   제외하므로 **화면에는 뜨지 않는다**
3. 보이지 않으니 커밋할 수도, 0.5.18의 되돌리기로 되돌릴 수도 없다

즉 "내 변경만" 필터는 **목록을 지키고 작업 트리는 지키지 않는다.** 미채택자 행이 쌓인다는 것은
아무도 볼 수 없고 아무도 치울 수 없는 더러운 파일이 쌓인다는 뜻이고, 여기에는 완화책이 없다.

### 1.4 되돌리기는 행을 못 닫아 반쪽이다

되돌리기 설계 2.1이 파일까지만 되돌리기로 정하면서, 열린 로그 행이 있는 항목은 되돌린 직후
다음 새로고침에 다시 추출되는 자기 취소를 받아들였다. 그 설계는 그 대가를 확인 문구에 적었고,
행을 닫는 동작은 이름부터 다르다("무시")며 여기로 미뤘다.

백로그는 이것이 개발자 20명의 1번 질문이 될 것이라고 적었다. README에 적어 두었지만 아무도
README를 읽지 않는다.

### 1.5 그래서 셋이 한 덩어리다

셋 다 "닫히지 않는 행"이라는 한 현상의 다른 얼굴이다. 하나만 고치면 나머지가 그 구멍을 되살린다.

- 4번만 고치면 → 1.2가 증가를 되살린다
- 5번만 고치면 → 닫힌 행이 여전히 무한히 쌓인다
- "무시"만 만들면 → 사람이 누르는 것만 닫힌다. 미채택자 행은 누를 사람이 없다

## 2. 결정

### 2.1 로그는 작업 큐다 — 나이 하나로 지운다

`DBVC_ChangeLog`는 감사 기록이 아니라 작업 큐다. 도구가 다시 읽지 않는 행에 보존 의무를 두지
않는다. 그래서 규칙은 한 줄이고 `IsProcessed`를 **보지 않는다**.

```sql
DELETE FROM dbo.DBVC_ChangeLog WHERE PostTime < DATEADD(day, -30, GETDATE());
```

열린 행을 지우는 것과 닫는 것은 도구 입장에서 효과가 같다 — 둘 다 `WHERE IsProcessed = 0`인
조회에서 빠진다. 지우는 쪽만 1.1의 증가를 실제로 멈추므로 `UPDATE`와 `DELETE`를 나눌 이유가 없다.
규칙이 하나면 1.2의 구멍도 생기지 않는다.

**30일은 고정 상수다.** 확장 속성으로 뺄 수 있지만 그렇게 하면 DB마다 값이 달라지고, 다른 이유를
아무도 기억하지 못하며, 화면에 드러나지 않아 "이 DB는 왜 다르게 동작하지"를 진단할 길이 없다.
스키마 버전과 `GRANT` 대상이 코드에 박혀 있는 것과 같은 판단이다. 필요해지면 그때 뺀다.

**무엇을 잃는지는 정확히 하나다.** 행이 지워져도 대부분 아무것도 잃지 않는다 — 이미 추출된
`.sql`이 더러우면 2.2의 표면화가 그 항목을 구제한다. 진짜 손실은 **파일을 지우는 근거**뿐이다.
`WorkingTreeCleaner`는 DROP 행을, 이름 변경 접기(`FoldRenames`)는 RENAME 행을 유일한 근거로 쓴다.
그 행이 아무도 새로고침하지 않은 사이에 지워지면 그 `.sql`은 저장소에 유령으로 남고, Git이 보기엔
clean이라 목록에도 뜨지 않는다.

그 창(窓)은 "DDL이 일어난 뒤 23명 중 아무도 한 번도 새로고침하지 않은 기간"이다. 그것이 30일이
되는 상황은 곧 아무도 DBVC를 쓰지 않는다는 뜻이다. N을 고르는 유일한 근거가 이것이므로 여기 적는다.

### 2.2 표면화는 사고가 아니라 계약이다

행이 사라지면 그 경로는 `PartitionByAuthor`의 `foreignPaths`에서도 빠진다. 그때부터
`BuildChangeSet`의 Git 폴백(`StateTracker.cs:770-796`)이 그 더러운 파일을 `LastLogId = 0`인
항목으로 목록에 올린다. 그래서 30일 뒤 1.3의 보이지 않던 파일이 **"주인 없는 변경"으로 떠오른다.**

- 누구나 커밋하거나 되돌릴 수 있다 — 미채택자의 변경이 결국 git에 담긴다
- `LastLogId == 0`이라 **되돌리기가 자기 취소되지 않는다**(되돌리기 설계 1.1이 예외로 적은 바로 그 부류)

이것은 지금 우연히 그렇게 동작하는 성질이다. 이 설계가 거기에 기대므로 **의도된 계약으로 승격하고
테스트로 고정한다**(4장). 고정하지 않으면 다음 사람이 폴백을 최적화하다 조용히 깬다.

부작용으로 N은 두 가지 시점이 된다: 표가 줄어드는 시점이자, 남의 변경이 표면에 뜨는 시점이다.
짧을수록 둘 다 좋고, 길수록 2.1의 유령 파일 위험과 "내가 안 만진 게 왜 떠"라는 혼란이 준다.

### 2.3 "무시" = 되돌리기 + 행 닫기, 한 번에

사용자가 치우고 싶은 것은 언제나 둘 다다. 행만 닫으면 더러운 `.sql`이 작업 트리에 남고, 파일만
되돌리면 다음 새로고침에 되살아난다. 각각 반쪽짜리이므로 한 동작으로 묶는다.

대가는 비가역성이다. 행을 닫는 것은 공유 DB에서 전역이고(되돌리기 설계 1.2), 닫힌 행이 가리키던
DB의 변경은 git에 담기지 않는다. 되돌리기보다 훨씬 센 동작이므로 확인 문구가 그만큼 무거워진다(3.6).

### 2.4 DBVC 소유 객체는 접두사로 가른다

새 프로시저(3.1)를 그냥 더하면 두 곳에서 깨진다. 트리거가 그 설치 DDL을 사용자 변경으로 기록하고,
"전체 다시 추출"이 그것을 `dbo/StoredProcedures/DBVC_PurgeChangeLog.sql`로 저장소에 커밋한다.
도구가 자기 자신을 버전 관리하게 된다.

지금 자기 제외는 이름 문자열 비교 두 곳이다 — 트리거의
`IN (N'DBVC_ChangeLog', N'trg_DBVC_DDL_Tracker')`와 `SmoManager.cs:622`의 테이블 이름 비교 하나.
객체가 늘 때마다 두 곳에 이름을 보태는 방식은 CLAUDE.md가 금지한 패턴("예외 메시지를 보고 하나씩
이름을 보태는 방식은 쓰지 않는다")과 같은 실수다.

**규칙: `DBVC_`로 시작하는 객체와 `trg_DBVC_DDL_Tracker`는 DBVC 소유다.** 추적하지도, 추출하지도
않는다. 9번(클라이언트 버전 보고)이 `DBVC_ClientVersion`을 더할 때 이 자리를 다시 열지 않아도 된다.

대가: 사용자가 진짜로 `DBVC_`로 시작하는 객체를 만들면 조용히 추적에서 빠진다. 접두사를 도구의
이름공간으로 선언하고 README에 적는다.

## 3. 설계

### 3.1 `DBVC_PurgeChangeLog` — 정리의 유일한 자리

```sql
CREATE PROCEDURE [dbo].[DBVC_PurgeChangeLog]
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @cutoff DATETIME = DATEADD(day, -30, GETDATE());
    DECLARE @deleted INT = 1;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (5000) FROM [dbo].[DBVC_ChangeLog] WHERE [PostTime] < @cutoff;
        SET @deleted = @@ROWCOUNT;
    END
END
```

**`EXECUTE AS OWNER` + `GRANT EXECUTE TO [public]`.** 그래야 `public`에 `DELETE`를 주지 않고도
정리가 돈다. 지금 `SELECT, UPDATE`만 주는 이유("주면 사용자가 로그를 직접 조작할 수 있다")가
그대로 유지되고, 사용자는 정책이 허용하는 것만 지울 수 있다.

**배치 루프는 장식이 아니다.** 이미 몇 달 쌓인 DB에서 첫 실행이 한 트랜잭션으로 돌면 수백만 행을
잠그고 트랜잭션 로그를 부풀린다. 그 순간 공용 개발 DB의 모든 DDL이 트리거의 `INSERT`에서 막힌다.
배치마다 커밋되므로 잠금이 짧고 로그가 재사용된다.

`PostTime` 단독 조건은 기존 인덱스(`IsProcessed, PostTime DESC`)로 seek이 되지 않는다.
`IX_DBVC_ChangeLog_PostTime`을 기존 인덱스들과 같은 `IF NOT EXISTS` 형태로 함께 만든다.

트리거와 같은 `DROP IF EXISTS` → `CREATE` 형태로 쓴다. `CREATE OR ALTER`는 SQL Server 2016 SP1+를
요구하므로 최소 버전을 새로 못 박게 된다.

**throttle은 두지 않는다.** 첫 실행이 표를 정리하고 나면 그다음부터는 인덱스 seek 하나에 삭제
0~수 행이다. 마지막 실행 시각을 확장 속성에 기록하는 장치는 그 비용을 아끼려고 상태를 하나 더
만드는 일이라 값을 하지 못한다.

### 3.2 자기 제외 — `DbvcOwnedObjects`

Core에 순수 함수 하나를 둔다. Git도 DB도 닿지 않으므로 판정의 시험대가 여기 하나다.

```
bool DbvcOwnedObjects.IsOwned(string objectName)
```

- `SmoManager`가 `:622`의 테이블 이름 비교 대신 이것을 부른다. 테이블 루프뿐 아니라 **모든 객체
  타입 루프**에 걸어야 한다 — 지금 프로시저 쪽에 제외가 없는 것이 2.4의 함정을 만들었다.
- 트리거는 SQL이라 이 함수를 부를 수 없다.
  `@ObjectName LIKE N'DBVC[_]%' OR @ObjectName = N'trg_DBVC_DDL_Tracker'`로 같은 판정을 쓰고,
  두 곳이 갈라지지 않는지는 `InstallScriptSyncTests`가 지금 추적 타입 목록을 대조하는 방식 그대로
  검사한다. (`LIKE`의 `[_]`는 `_`가 와일드카드이기 때문이다. 이스케이프를 빠뜨리면 `DBVCx...`까지
  제외된다.)

### 3.3 스키마 v6와 승급

`InstallTrigger.sql`의 확장 속성 값 `5` → `6`, `StateTracker.RequiredSchemaVersion` `5` → `6`.

기존 설치는 화면에 "변경 추적기 업데이트 필요"가 뜨고, 한 번 누르면 인덱스·프로시저·트리거 교체가
멱등으로 끝난다. 승급 경로를 새로 만들 것이 없다 — `InstallTrigger.sql`의 `IF NOT EXISTS` 보정
구조가 이미 그 일을 한다.

### 3.4 호출 지점

`StateTracker.RefreshState` 안에서 부르고, **자체 `try/catch`로 감싸 삼킨다.**
`ReconcileWithDatabase`의 선례를 그대로 따른다 — "대조하지 못하는 것이 새로고침을 무너뜨릴 이유는
되지 않는다"가 정리에도 똑같이 적용된다. 구버전(v5) DB에는 프로시저가 없어 `EXEC`이 실패하는데,
그 실패도 여기서 흡수되고 사용자에게는 업데이트 안내가 이미 따로 떠 있다.

`RefreshState`는 이미 `IBackgroundScheduler` 위에서 도므로 UI 스레드는 건드리지 않는다.

### 3.5 "무시" — 합성이지 새 기능이 아니다

새 Core 타입도 새 SQL도 만들지 않는다. 있는 것 둘의 합성이다.

```
IgnoreCommand(체크된 항목)
  1. DiscardPlan.Build(...) → IGitManager.DiscardChanges     (0.5.18의 되돌리기 그대로)
  2. IStateTracker.MarkProcessed(선택 레코드)                (커밋이 쓰는 그것 그대로)
  3. 새로고침
```

`MarkProcessed`가 이미 필요한 의미론을 갖고 있다. 현재 사용자가 아니라 **레코드의 작업자**로 행을
좁히고(전체 보기에서 남의 변경을 대신 커밋하는 경로 때문에 그렇게 되어 있다), `LastLogId == 0`인
항목은 스스로 건너뛴다 — 닫을 행이 없는 항목은 되돌리기만으로 끝나고 자기 취소도 없다.

**순서는 파일 먼저, 행 나중.** 커밋 흐름이 "커밋 → `MarkProcessed`"이고 실패 문구
(`BuildMarkProcessedFailureMessage`)가 그 순서를 전제로 쓰여 있다. 같은 순서를 쓰면 그 경로와 문구를
그대로 재사용한다. 두 갈래 실패가 모두 복구 가능하다:

| 실패 | 결과 | 복구 |
| --- | --- | --- |
| 되돌리기 실패 → 중단 | 아무 일도 일어나지 않는다 | 다시 누른다 |
| 되돌리기 성공, `MarkProcessed` 실패 | 파일은 깨끗, 행은 열림 → 다음 새로고침에 되살아난다 | 기존 실패 문구가 정확히 이 상황을 설명한다 |

반대 순서는 행이 닫힌 채 더러운 파일이 남아 2.2의 표면화로 떠오른다. 복구는 되지만 사용자가
이해할 수 없는 상태다.

**게이트는 되돌리기와 같다.** `PanelSelector`가 Write 클론에만 목록을 렌더링하므로 배포·감사
클론에는 애초에 없고, `IsBlocked`인 Write 클론에서도 끈다. 되돌리기 설계 2.3의 판단을 그대로
물려받고 새 예외를 만들지 않는다.

**남의 항목도 막지 않는다.** "내 변경만"이 기본이라 평소에는 내 것만 보인다. 전체 보기를 명시적으로
켠 사람에게만 남의 항목이 보이고, 그 사람은 이미 남의 변경을 대신 커밋할 수 있다. 커밋은 되는데
무시는 안 되면 비대칭이 생기고, DBA 3명의 청소 용도가 사라진다. 대신 확인 문구가 그 사실을 센다.

### 3.6 문구

되돌리기 설계 3.6의 문구에 **전역성**을 더한다. 세 가지를 반드시 말한다.

1. 파일에 무슨 일이 일어나는가 — `DiscardPlan`의 세 갈래 그대로(되돌림 / 되살림 / **삭제**)
2. **행이 닫히는 것은 이 데이터베이스를 함께 쓰는 모두에게 적용된다.** 그 변경은 앞으로 어떤
   새로고침에도 다시 오르지 않고, git에 담기지 않는다
3. 데이터베이스의 변경 자체는 그대로 남는다 — 무시는 DDL을 취소하지 않는다

남의 항목이 섞이면 한 줄을 더한다: "선택한 항목 중 N개는 다른 사람(`LoginName`)의 변경입니다."

문구 조립은 순수 함수로 뺀다. SQL Server 없이 검증하려고 `BuildMarkProcessedFailureMessage`를
뺀 것과 같은 이유다.

버튼 둘이 나란히 서므로 차이가 ToolTip 한 줄로 읽혀야 한다.

- **되돌리기** — "파일만 되돌립니다. 데이터베이스의 변경은 남아 다음 새로고침에서 다시 추출됩니다."
- **무시** — "파일을 되돌리고 변경 로그에서 닫습니다. 이 데이터베이스를 함께 쓰는 모두에게 적용됩니다."

## 4. 검증

SQL Server 없이 도는 CI가 제약이다. 판정을 순수 함수로 밀어 넣고, SQL이 필요한 것만 통합
테스트로 남긴다(붙지 않으면 Skip).

**SQL 없이 — CI가 실제로 지키는 것**

| 테스트 | 지키는 것 |
| --- | --- |
| `IsOwned_ReturnsTrue_WhenNameStartsWithDbvcPrefix` 외 | 접두사 규칙, `[_]` 이스케이프 포함 |
| `InstallScript_ExcludesTheSameObjectsCoreCallsItsOwn` | 트리거의 SQL 조건과 C# 판정이 갈라지지 않음 (`InstallScriptSyncTests`의 기존 서술형 이름 규칙을 따른다) |
| `BuildChangeSet_SurfacesForeignFile_WhenLogRowIsGone` | 2.2의 계약 |
| `IgnoreConfirmation_MentionsGlobalEffect_WhenRowsWillClose` | 전역성 문단이 빠지지 않음 |
| `IgnoreConfirmation_CountsForeignItems_WhenOthersChangesSelected` | 남의 항목 경고 |
| `Ignore_ClosesLogRows_AfterDiscardSucceeds` | 3.5의 순서 |
| `Ignore_DoesNotCloseRows_WhenDiscardFails` | 실패 시 중단 |

표면화 테스트가 특히 중요하다. 지금은 우연히 그렇게 동작하는데 이 설계가 거기에 기댄다.

**SQL Server가 필요한 것** — `DdlTriggerIntegrationTests`에 더한다.

- `Purge_DeletesRows_WhenOlderThanRetention`
- `Purge_KeepsRows_WhenWithinRetention`
- `Purge_DeletesUnprocessedRows_WhenOlderThanRetention` — 2.1의 핵심 규칙
- `Purge_DeletesAllRows_WhenCountExceedsBatchSize` — `INSERT ... SELECT TOP 5001 FROM sys.all_columns`로
  배치 루프를 태운다
- `Purge_Succeeds_WhenCallerIsNotOwner` — 기존 `LowPrivTable` 저권한 경로를 재사용해
  `EXECUTE AS OWNER` + `GRANT EXECUTE`를 확인
- `Trigger_DoesNotLog_WhenDbvcOwnedObjectIsCreated` — 설치가 자기 자신을 로그에 남기지 않는다

**회귀:** `RequiredSchemaVersion`을 6으로 올리면 깨지는 곳은 하나뿐이다 —
`StateTrackerTests.RequiredSchemaVersion_IsFive`(`:649`)가 값 `5`를 직접 단언한다. 이름과 값을
함께 `RequiredSchemaVersion_IsSix`로 바꾼다.

나머지는 따라온다. `InstallScriptSyncTests.InstallScript_StampsTheVersionCoreRequires`와
`DdlTriggerIntegrationTests`(`:368`)는 리터럴이 아니라 상수를 읽으므로 손댈 것이 없다 —
스키마 버전 대조 테스트를 새로 만들 필요가 없는 이유다.

### 4.1 CI가 검증하지 못하는 것

CLAUDE.md가 못 박은 영역이다. 구현이 끝나도 SSMS 21에서 아래를 눌러 보기 전에는 "동작한다"고
말하지 않는다.

- 무시 버튼이 되돌리기 옆에 뜨고, 배포·감사 클론에는 뜨지 않는다
- 확인 대화상자에 전역성 문단이 실제로 보인다
- 무시 → 항목이 사라지고, 새로고침해도 돌아오지 않는다
- 구버전(v5) DB에 연결하면 "변경 추적기 업데이트 필요"가 뜨고, 누르면 v6로 올라간다

## 5. 범위 밖

- **추출을 작업자로 좁히는 것.** 1.3의 근본 원인이지만 `StateTracker.cs:360`이 적은 반대 이유
  (남의 변경을 빼면 파일이 낡은 채로 남아 다음 커밋에 섞인다)가 여전히 유효하다. 2.2의 표면화가
  피해를 유한하게 만드는 것으로 이번에는 충분하다.
- **보존 기간을 설정으로 빼는 것.** 2.1의 이유로 하지 않는다.
- **무시를 배포·감사 클론에 여는 것.** 백로그 8번의 자리다. 3.5의 게이트를 그대로 둔다.
- **감사용 아카이브 테이블.** 로그는 작업 큐다(2.1). 필요해지면 별도 설계다.

## 6. 릴리스

사용자 눈에 보이는 동작이 바뀌므로 함께 고친다.

- `README.md` — 무시 버튼, 30일 보존 정책, `DBVC_` 접두사가 도구의 이름공간이라는 선언
- `docs/setup-checklist.md` — v6 승급
- `src/DBVC.Vsix/source.extension.vsixmanifest` — 버전
- `docs/team-rollout-backlog.md` — 4번·5번을 닫고, 2번이 미뤄 둔 "무시"가 여기서 끝났음을 적는다
