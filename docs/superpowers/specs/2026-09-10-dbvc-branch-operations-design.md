# 브랜치 조작 설계 — 티켓 한 바퀴를 SSMS 안에서 돈다

> **성격: 기능 추가.** `IGitManager`에 브랜치 셋을 더하고, `DbvcOperation`·`MappingPolicy`·
> 화면이 함께 움직인다. 병합·충돌·삭제는 그대로 도구 밖에 둔다.

## 1. 문제

팀은 지라 티켓 번호로 브랜치를 만든다. 소스 코드가 이미 그 규칙이고, DB 변경도 같은
티켓에 붙이려 한다. 그리고 **끝나지 않은 티켓의 DB 변경이 `develop`에 미리 들어가면
곤란하다** — 배포 클론이 `develop`에 고정되어 있어, 그 순간 DBA의 차이 검사 화면에
아직 나가면 안 되는 것이 `배포 필요`로 뜬다.

그래서 스키마 저장소에도 티켓 브랜치가 필요하다. 그런데 지금 DBVC는 브랜치를 만들지도
갈아타지도 못하고, 갓 만든 브랜치는 Push조차 되지 않는다.

### 1.1 티켓 한 바퀴에 터미널이 두 번 필요하다

| | 하는 일 | 지금 어디서 |
| --- | --- | --- |
| 1 | `develop`에서 `PROJ-123` 브랜치를 만든다 | **터미널** |
| 2 | 스키마 변경 → 새로고침 → 커밋 | DBVC |
| 3 | 첫 Push — 추적 브랜치가 없어 거부된다 | **터미널** (`git push -u`) |
| 4 | MR → `develop`에 병합 | GitLab |
| 5 | 다음 티켓을 위해 `develop`으로 돌아와 Pull | **터미널** |

3번은 `GitManager.cs`의 `ValidateRemoteAndBuildGuidance`가 통신 전에 막는다. 안내 문구는
친절하지만 결론은 "터미널에서 `git push -u origin PROJ-123`을 하고 오세요"다.

**이 셋이 도입 저항이 된다.** SSMS 안에서 끝난다고 해 놓고 티켓마다 두 번 나가게 하면,
"그럴 바에 처음부터 Git 클라이언트를 쓰지"가 된다.

### 1.2 1·3만 없애면 고리가 닫히지 않는다

브랜치 생성과 upstream 설정만 넣으면 5번이 남는다. 티켓마다 한 번은 여전히 터미널이다.
**전환을 넣어야 한 바퀴가 닫힌다.**

## 2. 결정

### 2.1 전환은 작업 트리가 깨끗할 때만 한다

브랜치 전환을 도구에 넣지 않기로 했던 사유는 이것이었다
([`rollout-announcement.md`](../../rollout-announcement.md) 2절):

> 넣으면 미커밋 변경이 있는 채로 갈아타는 사고를 도구가 거들게 된다.

**그 사유는 "전환"이 아니라 "더러운 채로 전환"을 가리킨다.** 미커밋 변경이 없으면
갈아타도 잃을 것이 없다. 그래서 전제를 하나 걸고 연다 — **미커밋 변경이 하나라도 있으면
거부하고, 무엇이 남았는지 보여 준다.**

이 규칙은 타협이 아니라 이 기능의 안전 속성이다. 새로고침이 `.sql`을 쓰므로 DBVC를 쓰는
동안 작업 트리는 자주 더럽고, 전환하려면 먼저 커밋하거나 되돌려야 한다. **막고 싶던
사고가 구조적으로 일어나지 않는다.**

그리고 실제로 전환이 필요한 시점(1.1의 5번)에는 트리가 깨끗하다 — MR을 올렸다는 것이
다 커밋하고 Push했다는 뜻이기 때문이다. 규칙이 막는 경우와 필요한 경우가 겹치지 않는다.

### 2.2 생성은 HEAD에서만 한다

`git checkout -b`를 HEAD에서 하면 **작업 트리가 바뀌지 않는다.** 같은 커밋에 이름표를
하나 더 붙이고 HEAD를 그쪽으로 옮길 뿐이다. 더러운 트리에서도 안전하므로 2.1의 전제를
걸지 않는다.

임의의 커밋·브랜치에서 분기하는 것은 넣지 않는다. 필요한 것은 "지금 자리에서 티켓
브랜치를 딴다" 하나다.

### 2.3 upstream은 조용히가 아니라 물어서 설정한다

지금 코드의 주석은 이렇게 적고 있다.

> 추적을 대신 설정해 주지는 않는다. 버튼 하나가 사용자의 git config를 조용히 바꾸면 안 된다.

**지키려던 것은 "조용히"이지 "설정하지 않는다"가 아니다.** 사용자가 Push를 누른 것 자체가
"이 브랜치를 원격에 올리겠다"는 의사표시다. 그 의사를 받고도 터미널로 돌려보내는 것은
원칙을 지키는 가장 비싼 방법이다.

그래서 거부 대신 **확인을 받는다.** 무엇을 하는지 그대로 적어 보여 주고, 동의하면 실행한다.

### 2.4 병합·충돌·삭제는 그대로 밖이다

`develop`에 넣는 것은 GitLab MR의 일이다. 충돌 해결은 GitLab이 더 잘한다. 브랜치 삭제는
급하지 않고, 잘못 지우면 되돌리기 어렵다. **셋 다 이번 범위가 아니다.**

## 3. 설계

### 3.1 `MappingPolicy` — 두 동작을 표에 올린다

`DbvcOperation`에 `CreateBranch`, `SwitchBranch`를 더하고 **`Write`에서만 허용**한다.

```csharp
case DbvcOperation.CreateBranch:
case DbvcOperation.SwitchBranch:
    // 배포·감사 클론은 고정 브랜치가 필수다(MappingConfig.Branch). 브랜치를 옮기는
    // 순간 비교 기준이 무너지고, RepositoryStateEvaluator가 BranchMismatch로 화면을 덮는다.
    // 즉 허용해도 곧바로 막히는 동작이라, 표에서 먼저 끊는다.
    return mode == MappingMode.Write;
```

`IsAllowed`의 `default`가 예외를 던지므로, 열거형만 늘리고 표를 고치지 않으면 조용히
허용되는 대신 죽는다. 그 성질을 그대로 쓴다.

`PushChanges`가 이미 `DbvcOperation.Push`로 막히므로 upstream 설정은 새 동작이 아니다 —
Push의 한 갈래다.

### 3.2 `IGitManager` — 더하는 표면

```csharp
/// 로컬 브랜치와 원격 추적 브랜치를 한 벌로 낸다. 현재 브랜치에 표시가 붙는다.
IReadOnlyList<BranchInfo> GetBranches(string serverName, string databaseName);

/// HEAD에서 브랜치를 만들고 체크아웃한다. 작업 트리는 바뀌지 않는다.
BranchResult CreateBranch(string serverName, string databaseName, string branchName);

/// 브랜치를 갈아탄다. 미커밋 변경이 있으면 갈아타지 않고 그 목록을 담아 돌려준다.
BranchResult SwitchBranch(string serverName, string databaseName, string branchName);

/// setUpstream이 true이면 추적이 없는 브랜치를 원격에 만들고 추적을 설정한다.
PushResult PushChanges(string serverName, string databaseName, bool setUpstream = false);
```

`PushChanges`는 **기본값을 붙여 기존 시그니처를 유지한다.** `IGitManager` 구현이 테스트
이중체로 여러 곳에 있고, 이 변경의 본질은 새 갈래이지 호출부 전면 수정이 아니다.

`PushResult`에 값 하나를 더한다.

```csharp
/// 현재 브랜치에 추적 중인 원격 브랜치가 없다. 화면이 확인을 받아 setUpstream: true로 다시 부른다.
NoUpstream
```

**이 경우의 동작이 바뀐다.** 지금은 `ValidateRemoteAndBuildGuidance`가
`GitRemoteNotConfiguredException`을 던지고 화면이 그 한국어 문구를 그대로 띄운다. 앞으로는
`PushChanges`가 그 공용 검사를 부르기 **전에** `repo.Head.IsTracking`을 보고 `NoUpstream`을
반환한다. **첫 Push는 예외적인 사건이 아니라 정상 흐름이므로 결과값이 맞다.**

`PullChanges`는 그대로 던진다. 추적이 없는 브랜치에는 받아올 대상 자체가 없어 안내가
종착점이고, 사용자가 이어서 할 일이 없다.

바꾸는 것이 둘이다.

- **호출부는 하나뿐이다** — `ViewChangesViewModel`의 Push 명령. 다른 곳에서 이 예외에
  기대고 있지 않다.
- **`GitManagerTests.PushChanges_ExplainsInKorean_WhenTheCurrentBranchHasNoUpstream`이 옛
  동작을 검증하고 있다.** 지우지 않고 새 동작으로 고친다 — 무엇이 언제 왜 바뀌었는지 그
  테스트가 이력에 남긴다.

`BranchResult`는 되돌리기의 `DiscardResult`와 같은 자리를 차지한다: 성공 여부와, 실패했다면
사람이 읽을 사유와 근거 목록.

```csharp
public class BranchResult
{
    public bool Succeeded { get; set; }
    /// 실패 사유. 성공이면 null이다. 한국어이며 화면이 그대로 띄운다.
    public string? Message { get; set; }
    /// 전환이 미커밋 변경 때문에 거부된 경우 그 파일들. 그 외에는 빈 목록이다.
    public IReadOnlyList<string> BlockingPaths { get; set; }
}
```

### 3.3 판정은 Core에 둔다

전환 가부는 화면이 아니라 Core가 정한다. `GitManager.SwitchBranch`가
`repo.RetrieveStatus()`로 미커밋 변경을 세고, 하나라도 있으면 체크아웃을 **시도하지 않고**
`BlockingPaths`에 담아 돌려준다. libgit2의 `CheckoutConflictException`에 기대지 않는다 —
그것은 겹치는 파일이 있을 때만 나므로, 겹치지 않는 변경은 조용히 딸려가 버린다.

`.gitattributes` 같은 DBVC가 만든 파일도 미커밋이면 똑같이 막는다. 예외를 두면 "무엇은
따라오고 무엇은 안 따라오는지"를 사용자가 알아야 한다.

### 3.4 화면

도구 창 위쪽, 지금 `브랜치: develop`이 있는 자리에 버튼 둘을 붙인다.

- **`새 브랜치`** — 이름을 받는 작은 대화상자. 만들고 체크아웃한 뒤 상태를 다시 읽는다.
- **`브랜치 전환`** — `GetBranches`로 목록을 띄우고 고르게 한다. 거부되면 `BlockingPaths`를
  그대로 보여 주며 "먼저 커밋하거나 되돌리세요"로 안내한다.

둘 다 `MappingPolicy.IsAllowed`로 `CanExecute`를 막는다. Core와 화면이 같은 함수를 부른다는
기존 규약을 따른다.

Push는 `NoUpstream`을 받으면 확인 대화상자를 띄운다.

> `PROJ-123` 브랜치는 아직 원격에 없습니다.
> `origin/PROJ-123`을 만들고 이 브랜치가 그것을 추적하도록 설정합니다.
> 이 저장소의 `.git/config`만 바뀌며 다른 저장소에는 영향이 없습니다.

동의하면 `PushChanges(..., setUpstream: true)`로 다시 부른다.

브랜치 조작은 네트워크를 타지 않으므로(`CreateBranch`·`SwitchBranch`) 배경 스케줄러가
필요 없다. Push는 지금처럼 `IBackgroundScheduler`로 돈다.

### 3.5 지라 연결은 코드가 아니다

브랜치 이름과 커밋 메시지 **둘 다** 티켓 키로 시작하게 문서에 규칙으로 둔다
(`PROJ-123`, `PROJ-123: 주문 상세에 할인율 컬럼을 더한다`). 연동이 브랜치를 긁든 커밋을
긁든 걸린다.

**이름 규칙을 도구가 검증하지 않는다.** `PROJ-\d+`를 코드에 넣으면 조직이 규칙을 바꾸는 날
도구가 걸림돌이 된다.

**스키마 저장소에도 GitLab↔지라 연동을 걸어야 한다.** 소스 코드 저장소와 다른 저장소이므로
자동으로 따라오지 않는다. 이것은 GitLab 프로젝트 설정이다.

## 4. 검증

**단위 테스트**

- `MappingPolicyTests` — 새 동작 2개 × 모드 3개 = 6건. 순수 함수다.
- `GitManagerTests` — `Repository.Init`으로 임시 저장소를 만드는 기존 방식을 그대로 쓴다.
  - `CreateBranch_KeepsWorkingTree_WhenTreeIsDirty`
  - `SwitchBranch_Refuses_WhenTreeIsDirty` — `BlockingPaths`에 그 파일이 담기는 것까지
  - `SwitchBranch_Switches_WhenTreeIsClean`
  - `SwitchBranch_Refuses_WhenTargetDoesNotExist`
  - `PushChanges_ReturnsNoUpstream_WhenBranchIsNotTracking`
  - `PushChanges_SetsUpstream_WhenSetUpstreamIsTrue` — 원격은 로컬 bare 저장소로 세운다
- ViewModel — `CanExecute`가 `Deploy`·`Audit`에서 false인지.

**CI가 검증하지 못하는 것**

WPF 대화상자, VS 패키지 로딩, SSMS 안에서의 실제 동작. **SSMS 21에서 직접 눌러 보기 전에는
"동작한다"고 말하지 않는다.** 최소한 이 넷을 손으로 밟는다.

1. `develop`에서 `새 브랜치` → 미커밋 추출물이 그대로 남아 있는지
2. 커밋 → Push → 확인 대화상자 → `origin/PROJ-123`이 생기고 두 번째 Push는 묻지 않는지
3. 추출물이 남은 채 `브랜치 전환` → 거부되고 파일 이름이 보이는지
4. 배포 클론에서 두 버튼이 아예 뜨지 않는지

## 5. 범위 밖

- **병합·충돌 해결** — GitLab의 일이다.
- **브랜치 삭제** — 잘못 지우면 되돌리기 어렵고, 급하지 않다.
- **원격 브랜치 목록 자동 갱신** — `원격 확인`이 이미 누를 때만 네트워크를 쓴다. 같은 규칙을
  따라 `GetBranches`도 마지막 fetch 기준의 로컬 값을 낸다.
- **force push** — 없다.
- **브랜치 이름 규칙 검증** — 3.5.
- **경고 A(미승격 변경 경고)** — 백로그 11번. 브랜치를 오래 들고 있을수록 필요해지지만
  이번 범위가 아니다.

## 6. 이 변경이 키우는 위험

브랜치를 쓰기로 한 이상, **오래 들고 있을수록 공용 개발 DB에서 남의 변경이 딸려 올 확률이
커진다**(경고 B, `CoAuthorDetector`). 도구가 브랜치를 쉽게 만들어 주면 브랜치가 늘고, 늘면
이 위험도 는다.

도구로 줄일 수 없다 — 공용 개발 DB가 하나뿐이라는 전제에서 나오는 것이라 "티켓을 빨리
닫는다"로만 관리된다. 문서에 이미 적혀 있고, 이 기능을 배포할 때 다시 짚는다.

## 7. 문서

사용자 눈에 보이는 동작이 바뀌므로 함께 고친다.

- [`user-guide.html`](../../user-guide.html) — 2장에 티켓 한 바퀴를 절차로. 4장 증상표에서
  "Push가 추적 브랜치가 없다며 거부됩니다"를 새 동작에 맞게.
- [`rollout-announcement.md`](../../rollout-announcement.md) — 2절 브랜치 규칙에 티켓 키
  규칙과 새 버튼.
- [`README.md`](../../../README.md), `source.extension.vsixmanifest` 버전.
