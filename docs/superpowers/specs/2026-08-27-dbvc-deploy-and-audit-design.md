# DBVC 배포와 감사 — 3차 설계

상위 설계는 `2026-08-24-dbvc-git-workflow-design.md`다. 이 문서는 그 7.3(3차)을 구현 가능한
수준으로 좁힌다. 1차(작업자 필터·`CREATE OR ALTER` 추출·매핑 확장·브랜치 이탈 차단)와
2차(`Clone`·`Fetch`)는 끝나 있다.

## 1. 범위

상위 설계 7.3의 넷 중 **셋**을 담는다.

- 3.3 mode별 허용 동작과 `deploy`·`audit` 화면
- 3.6 배포 스크립트 분류, 3.7 배포 3단계 루프
- 3.8 운영 드리프트 검사

**경고 A(3.10)는 담지 않는다.** 그것은 개발 클론의 커밋 경로에 붙는 것이라 화면도 코드 경로도
여기와 겹치지 않는다. 별건으로 다룬다.

**3.8은 별도 기능이 아니다.** `audit` mode에서 도는 같은 차이 검사이고 결과 문구만 다르다
(3.4.4). 검사 주기는 도구가 아니라 `docs/setup-checklist.md`의 운영 규칙이 정한다.

## 2. 상위 설계에서 바뀌는 것

구현하며 드러난 두 가지가 상위 설계의 표를 고친다. 근거는 각 절에 있다.

| 상위 설계 | 이 문서 | 사유 |
|---|---|---|
| 3.3 표: `deploy`·`audit`의 추출 "O (전체만)" | **X** | 비교가 저장소에 한 글자도 쓰지 않는다(3.1). 배포·감사 클론이 저장소에 쓸 일이 없어졌다 |
| 3.6: 제외 대상은 "테이블" | **`CREATE OR ALTER` 미지원 타입 전부** | T-SQL의 `CREATE OR ALTER`는 프로시저·뷰·함수·트리거에만 있다. `Sequence`·`Synonym`·`UserDefinedType`도 대상에 있으면 그대로 실행되지 않는다(3.3) |

## 3. 설계

### 3.1 비교 엔진 — 추출 루프 안에서 판정만 한다

`SmoManager.ScriptAll`은 이미 객체마다 임시 스테이징 폴더에 스크립팅한 뒤
`PublishIfChanged`가 `HasSameBytes`로 저장소 파일과 바이트를 비교하고, 같으면 복사하지 않는다.
**차이 판정에 필요한 것의 절반이 이미 그 루프 안에서 돌고 있다.** 진행률·취소·객체별 실패
격리도 같은 루프에 붙어 있다.

그래서 저장소에 전체 추출을 하고 `git status`로 읽거나(작업 트리를 덮어쓰고 `reset --hard`로
되돌려야 한다), 스크래치 폴더에 한 벌 더 뜬 뒤 폴더끼리 비교하는(경로 집합 diff와 텍스트
정규화를 새로 쓴다) 대신, **반영 단계만 갈아 끼운다.**

`ScriptAll`에서 `PublishIfChanged` 호출부를 델리게이트로 뽑는다. 기본 전략은 지금의 publish,
비교 전략은 판정을 클로저의 리스트에 담고 파일을 쓰지 않는다. 루프·취소·진행률·실패 격리가
한 벌로 남는다.

```csharp
public enum ObjectDiffState
{
    Modified,           // 양쪽에 있고 바이트가 다르다
    MissingInDatabase,  // 브랜치에만 있다 — 배포되지 않았다
    MissingInBranch     // DB에만 있다 — 커밋되지 않았거나 무단 추가다
}

public sealed class SchemaDifference    // QualifiedName, RelativePath, ObjectType, State
public sealed class ComparisonResult    // Differences, FailedObjects, ComparedCount, IsInSync
```

`ISmoManager`에 두 메서드가 는다. 기존 API의 `(serverName, databaseName)` 관례를 그대로 따른다.

```csharp
/// 저장소에 쓰지 않고 대상 DB와의 차이만 판정한다. 취소는 OperationCanceledException으로 전파된다.
ComparisonResult? CompareWithRepository(
    string serverName, string databaseName,
    IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken);

/// 객체 하나를 스크립팅해 텍스트로 돌려준다. 파일을 쓰지 않는다 — diff 본문 전용이다.
string? ScriptObjectToText(string serverName, string databaseName, string qualifiedName);
```

**`Same`은 결과에 담지 않는다.** 수천 개가 되고 화면에 쓸 데가 없다. 대신 `ComparedCount`가
"n개 중 m개 차이"를 만든다.

**"브랜치에만 있음" 판정은 순수 함수로 분리한다.** 저장소를 재귀 스캔해 `.sql`을 모으는 것은
얇은 어댑터가 하고, `추출된 경로 집합 + 저장소 경로 목록 → MissingInDatabase 목록`은 파일
시스템에 닿지 않는 함수로 둔다. `ObjectPathConvention.TryParseRelativePath`로 규약 밖 파일을
거른다 — 저장소에 사람이 둔 잡다한 `.sql`이 "DB에 없는 객체"로 보고되면 안 된다.

**diff 본문은 클릭한 객체 하나만 다시 뜬다.** 비교 중에 뜬 텍스트는 그 자리에서 버려진다
(지금도 스테이징 파일은 객체마다 지운다). 사용자가 목록에서 항목을 고르면 그 객체 하나를
스크립팅해 텍스트로 돌려주는 `ScriptObjectToText`를 부른다 — `ScriptObjectsDetailed`의 필터
경로가 이미 객체 이름 목록을 받으므로 스테이징까지의 흐름을 그대로 쓴다.

#### 전제 두 개를 검사로 세운다

**작업 트리가 깨끗해야 한다.** 비교 기준은 "브랜치의 내용"인데 실제로 읽는 것은 작업 트리
파일이다. 미커밋 편집이 있으면 그것이 브랜치인 척한다. `RepositoryBlockReason`에
`WorkingTreeDirty = 4`를 더한다. **`mode != write`일 때만 발동한다** — 개발 클론에서 더러운
트리는 추출 직후의 정상 상태다. 따라서 `RepositoryStateEvaluator.Evaluate`가 `mode`를 받는다.
값이 4인 이유는 그 enum의 순서가 곧 우선순위이기 때문이다. 병합 중·detached·브랜치 불일치가
겹치면 그쪽을 먼저 알린다.

**낡은 브랜치로 비교하지 않는다** (상위 3.6의 "배포 전 Pull 강제"). 차이 검사 1단계에서 Pull을
먼저 돌린다. 로컬 `develop`이 낡았으면 방금 병합된 변경이 차이 목록에서 통째로 빠지고, 그것은
"배포 완료"로 보인다. 원격이 없으면 건너뛰고, 원격이 있는데 실패하면 `RemoteDiagnostics`가
만든 사유를 띄우고 멈춘다.

### 3.2 mode 시행

판정은 순수 함수 하나에 모은다.

```csharp
public enum DbvcOperation { InstallTracker, Extract, Commit, Push, Compare, GenerateScript }
public static class MappingPolicy { public static bool IsAllowed(MappingMode mode, DbvcOperation op); }
```

| mode | InstallTracker | Extract | Commit | Push | Compare | GenerateScript |
|---|---|---|---|---|---|---|
| `write` | O | O | O | O | X | O (기존 참고용) |
| `deploy` | X | X | X | X | O | O |
| `audit` | X | X | X | X | O | O |

Pull과 이력·diff 보기는 읽기라 표에 넣지 않고 모든 mode에서 허용한다 — 배포 클론은 오히려
Pull을 해야 한다.

`write`의 `Compare`가 X인 이유는 개발 DB가 정의상 `master + 진행 중인 모든 feature` 상태라
브랜치와의 차이 전체가 잡음이기 때문이다.

`audit`의 `GenerateScript`는 상위 설계에서 "선택"이었는데 **허용한다.** 분류 로직이 `deploy`와
한 글자도 다르지 않고, 결과물은 동작이 아니라 텍스트 파일이다. 막으면 안전이 늘지 않고 분기만
는다.

**두 층에서 막되 판정은 한 곳이다.** VM은 `CanExecute`로 버튼을 죽이고, Core API
(`InitializeDatabase`, `ScriptObjects*`, `CommitChanges`, `PushChanges`, 그리고 새로 나는
`CompareWithRepository`)는 진입부에서 같은 함수로 한 번 더 확인한다. 셋 다 이미
`IConfigManager`를 들고 있어 매핑에서 mode를 읽는다.
위반은 조용한 `false`가 아니라 `OperationNotAllowedException`(한국어 사유)으로 던진다 —
버튼을 죽이는 것만으로는 나중에 코드 경로가 하나 늘 때 조용히 다시 열린다.

`mode`는 실수를 막는 장치이지 보안 장치가 아니다. `mappings.json`은 사용자가 편집할 수 있는
로컬 파일이다. 실제 권한은 SQL Server 계정 권한이 담당한다.

#### mode와 고정 브랜치를 받는 자리

2차의 `RepositoryConnectRequest`에 `Mode`와 `Branch`를 더하고, 대화상자에 용도 선택(개발 /
배포 / 감사)과 고정 브랜치 입력을 넣는다. 고정 브랜치는 연결 직후 저장소의 현재 브랜치로
기본값을 채운다. **`deploy`·`audit`을 고르면 비울 수 없다** — 고정 없는 배포 클론은 상위 3.4가
막으려던 사고를 그대로 허용한다.

손편집으로 두지 않는 이유는 오타 한 글자가 `MappingModeConverter`에 의해 `Audit`으로
떨어지고(그것이 안전한 기본값이다), 사용자에게는 "왜 아무것도 안 되지"로 보이기 때문이다.

### 3.3 배포 스크립트 분류

**분류 축은 "테이블인가"가 아니라 "`CREATE OR ALTER`를 지원하는 타입인가"다.** T-SQL의
`CREATE OR ALTER`는 프로시저·뷰·함수·트리거에만 있다. 테이블만 빼면 `Sequence`·`Synonym`·
`UserDefinedType`·`UserDefinedDataType`이 조용히 스크립트에 들어가 "이미 있습니다"로 실패한다.

지원 여부 표는 `ObjectPathConvention`에 둔다 — SMO 타입명과 DDL 트리거 `ObjectType`의 매핑이
이미 거기 한 곳에 있고, T-SQL 사양은 고정된 순수 표다.

| 차이 상태 | 타입 | 처리 |
|---|---|---|
| `MissingInDatabase` | 무관 | **포함** — 파일 내용 그대로. 신규라 테이블도 안전하다 |
| `Modified` | `CREATE OR ALTER` 지원 | **포함** — 파일이 이미 그 형태로 저장돼 있다(1차) |
| `Modified` | 미지원 | **제외** — `ManualChangeRequired` |
| `MissingInBranch` | 무관 | **제외** — `NotInBranch`. 담을 재료 자체가 없다 |

스크립트의 재료는 **브랜치의 파일**이다. 대상 DB에서 다시 뜨지 않는다. "`develop`에 병합된
것만 테스트에 나간다"를 검사가 아니라 배치로 지킨다 — 배포 클론은 `develop`에 고정되어 있고
병합 안 된 변경은 애초에 파일로 존재하지 않는다.

**제외 사유를 구분한다.** 지금 `ScriptExportResult.ExcludedObjects`는 `List<string>`이라 헤더에
`Excluded: 3 (a, b, c)`로만 찍힌다. 사용자가 할 일이 셋 다 다르다.

```csharp
public enum ScriptExclusionReason { NoContent, ManualChangeRequired, NotInBranch }
public sealed class ScriptExclusion { QualifiedName, Reason }
```

`NoContent`는 기존의 "스크립팅 불가"(파일이 없거나 비었다)다. `ScriptGenerator`의 헤더는
사유별로 묶어 적는다.

**헤더를 한국어로 옮긴다.** 지금 `DBVC Deployment Script` / `Generated` / `Objects` /
`Excluded`는 영어인데, 사유를 한국어로 적으면 한 헤더에 두 언어가 섞인다. CLAUDE.md의 규약은
사용자에게 보이는 모든 문구가 한국어라고 하고 이 스크립트는 사람이 열어 보는 산출물이다.
매번 새로 생성되는 것이라 포맷을 바꿔도 깨지는 것이 없다.

**분류는 순수 함수로 뽑는다** — `(ObjectDiffState, objectType) → ScriptDisposition`. DB도 파일도
없이 테스트된다.

**기존 경로는 그대로 둔다.** `write`의 참고용 `Export(IEnumerable<ChangeRecord>, ...)`는 살아
있고, 비교 결과를 받는 `ExportFromComparison(...)`을 같은 `ScriptExporter`에 더한다. 매핑
조회·파일 읽기·`ScriptGenerator` 호출을 공유한다.

### 3.4 화면

#### 3.4.1 새 ViewModel로 뺀다

`ViewChangesViewModel`은 1592줄이고 이미 대상 선택·접속·매핑·차단·busy·커밋·이력을 다 들고
있다. 배포/감사를 여기 얹으면 2천 줄이 된다. `DeploymentViewModel`이 차이 목록·검사 명령·
스크립트 저장 명령·선택 항목만 소유하고, 부모가 `Deployment` 속성으로 노출한다.

**그러려면 busy를 공유해야 한다.** 지금 `IsBusy`·`ProgressText`·`_cancellableWorkOutstanding`·
`CancelCommand`가 부모에 흩어져 있어, 자식이 그대로 쓰려면 부모를 역참조하게 된다. 작은
`BusyState` 객체로 뽑아 두 VM이 같은 인스턴스를 본다 — 도구 줄의 진행 표시와 취소 버튼은
여전히 하나다. 기존 파일에 손대는 유일한 리팩터링이고, 안 하면 순환 참조가 생긴다.

#### 3.4.2 패널 전환은 세 갈래다

지금 XAML은 `IsInitialized` / `!IsInitialized` 둘로만 갈린다.

| 조건 | 화면 |
|---|---|
| `write` · 초기화됨 | 지금의 변경 목록 |
| `write` · 미초기화 | 지금의 초기화 오버레이 |
| `deploy` / `audit` | **배포·감사 패널** — 초기화 여부와 무관 |

셋째 줄이 상위 1.4의 사고(DBA가 운영 DB에서 초기화 버튼을 눌러 금지된 DDL 트리거를 설치하는
것)를 막는 자리다. `IsBlocked` 차단 오버레이는 그 위를 그대로 덮는다.

컨버터를 늘리는 대신 VM이 `ShowChangeList` / `ShowSetupOverlay` / `ShowDeploymentPanel` 셋을
노출하고, 판정은 순수 함수로 두어 테스트한다.

#### 3.4.3 3단계 루프의 버튼은 둘뿐이다

1. `[차이 검사]` — Pull → 대상 DB 전체 비교(진행률·취소) → 차이 목록
2. `[배포 스크립트 저장]` — 3.3의 분류로 만들고 기존 `IFileSaveDialog`로 저장. 실행은 사람이
   SSMS 쿼리 창에서 한다
3. 다시 `[차이 검사]` — 0이면 "일치합니다"

**3단계는 새 화면이 아니라 같은 버튼을 다시 누르는 것이다.** 별도 단계로 만들면 오히려 안
눌린다. 그런데 이 3단계가 코드 배포에는 없던 단계이고 이 도구의 값어치가 나오는 지점이다 —
웹은 배포하면 같아지지만 DB는 "됐다고 생각했는데 안 된" 경우가 실재한다. 의존성 때문에
스크립트가 중간에 실패해도 조용히 성공한 척하지 않는다.

도구가 스크립트를 실행하지 않는 이유는 배포 실패의 책임과 부분 적용 상태를 도구가 지게 되기
때문이다. 파일까지가 도구의 몫이다.

#### 3.4.4 목록 문구는 mode에 따라 다르다

| `ObjectDiffState` | `deploy` | `audit` |
|---|---|---|
| `MissingInDatabase` | 배포 필요 (신규) | **확인 필요** |
| `Modified` | 배포 필요 (내용 다름) | **확인 필요** |
| `MissingInBranch` | DB에만 있음 | **확인 필요** |

운영에는 트리거를 설치할 수 없으므로 차이가 "미배포"인지 "무단 변경"인지 구분할 수 없다.
둘 다 "확인 필요"로 보고하고 판단은 DBA에게 맡긴다. **구분되는 척하지 않는다.**

항목을 고르면 왼쪽 브랜치 파일 / 오른쪽 DB 현재로 diff가 뜬다. 기존 `DiffService`·
`DiffTextBuilder`를 그대로 쓴다.

**대상이 바뀌면 차이 목록을 지운다** — 2차의 원격 확인 표시와 같은 규칙이다. 낡은 결과를
최신인 척 보여주지 않는다.

## 4. 검증

### 4.1 단위 테스트 (DB·Git 없이)

- `MappingPolicy.IsAllowed` 표 전체
- `RepositoryStateEvaluator`의 `WorkingTreeDirty` — `write`에서는 발동하지 않는가, 다른 사유와
  겹치면 우선순위가 지켜지는가
- `MissingInDatabase` 순수 함수 — 규약 밖 `.sql`이 걸러지는가
- `ObjectPathConvention`의 `CREATE OR ALTER` 지원 타입 표
- 분류 순수 함수 `(ObjectDiffState, objectType) → ScriptDisposition`
- `ScriptGenerator` — 사유별 제외 헤더, 한국어 헤더
- `ScriptExporter.ExportFromComparison` — 주입된 차이 목록으로 포함·제외가 갈리는가
- 패널 전환 판정
- 두 VM — `CanExecute`, 대상 변경 시 목록 초기화, `BusyState` 공유(자식이 일할 때 부모 버튼도
  잠기는가)

### 4.2 통합 테스트 (`SmoManagerIntegrationTests`, 로컬 SQL 없으면 Skip)

- 비교가 저장소에 **한 글자도 쓰지 않는가**
- 방금 추출한 저장소와 같은 DB를 비교하면 차이가 **0**인가 (거짓 양성)
- 객체를 `ALTER`한 뒤 비교하면 **그 객체만** `Modified`인가
- 저장소에서 파일을 지운 뒤 비교하면 `MissingInBranch`인가
- DB에서 `DROP`한 뒤 비교하면 `MissingInDatabase`인가
- **생성한 배포 스크립트가 객체가 이미 있는 DB에서 그대로 실행되는가**
- 비교 중 취소가 먹는가

### 4.3 SSMS 21에서 직접 눌러야 하는 것

CI가 검증하지 않는 영역이다. 여기를 건드렸다면 직접 눌러 보기 전에는 "동작한다"고 말할 수 없다.

- `deploy`·`audit`에서 초기화 오버레이가 뜨지 않는가
- 차단 오버레이가 배포 패널도 덮는가
- 전체 비교 중 SSMS가 잠기지 않는가
- 연결 대화상자의 용도 선택과 고정 브랜치 입력

## 5. 위험

**SMO 출력의 결정성.** 방식 전체가 "같은 객체를 두 번 뜨면 바이트가 같다"에 기댄다. 흔들리면
전부 `Modified`로 나와 화면이 무의미해진다.

이 가정은 이미 돌고 있다 — `PublishIfChanged`가 바이트가 같으면 쓰지 않으므로, 1차 이후
새로고침이 잡음 diff를 내지 않았다면 사실상 검증된 것이다. 4.2의 둘째 항목이 이것을 정면으로
확인한다. **깨지면 대비책은 텍스트 정규화(BOM·개행·후행 공백) 비교로 떨어뜨리는 것이다.**

## 6. 범위 밖

- **경고 A (상위 3.10).** 개발 클론의 커밋 경로에 붙는 별건이다
- **차이 계산 엔진 / 자동 `ALTER` 생성.** 상위 5장 그대로다. 기존 테이블에 컬럼을 더하는 것은
  기존 행을 무엇으로 채울지의 문제라 스키마만 보고 결정할 수 없다
- **의존성 해석.** 3.4.3의 3단계 루프가 실패를 드러낼 뿐 예방하지는 못한다
- **저장소 파일이 `CREATE OR ALTER`로 저장돼 있지 않은 경우의 사전 검사.** 1차 이후 저장소는
  항상 그 형태로 만들어진다. 손편집으로 어긋나면 3단계 루프가 드러낸다
- **드리프트 검사의 주기 실행.** 전체 비교는 무거워서 사용자가 모르는 사이에 서버를 치게
  되고, 폴링 없는 기존 설계 원칙과 어긋난다. 주기는 운영 규칙이 정한다

## 7. 문서

- `README.md`와 `docs/setup-checklist.md`에 세 폴더 배치(상위 3.1), 용도 선택, 상위 6장의 운영
  규칙을 싣는다
- `src/DBVC.Vsix/source.extension.vsixmanifest`를 **0.5.0**으로 올린다
