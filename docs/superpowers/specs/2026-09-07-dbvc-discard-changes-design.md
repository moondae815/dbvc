# 작업 트리 되돌리기 설계 — 개발자 20명이 SSMS 밖으로 나가지 않게 한다

## 1. 문제

`GitManager`에 discard/reset이 없다. 잘못 추출된 `.sql`을 버리려면 SSMS 밖에서
`git checkout --`을 쳐야 한다. DBA 3명은 알아도 개발자 20명에게는 벽이다
(`docs/team-rollout-backlog.md` 2번).

변경 목록에는 이미 체크박스가 있고 `CommitChanges`가 `relativePaths`로 선택분만 커밋한다.
되돌리기도 같은 선택 모델을 쓴다.

### 1.1 되돌리기는 대부분 다음 새로고침에 스스로 취소된다

이 설계의 출발점이다. `Refresh`가 하는 첫 일이 `Extract`이고, 그 대상은
`StateTracker.GetChangedObjectNames`가 내는 목록이다. 그것은 `IsProcessed = 0`인 로그 행이
가리키는 객체다(`StateTracker.cs:362` → `ReadPendingRows`, `WHERE IsProcessed = 0`).

**따라서 열린 로그 행이 있는 항목은 되돌린 직후 다시 추출되어 도로 더러워진다.** 새로고침은
사용자가 누를 때뿐 아니라 연결·초기화·커밋 뒤에도 자동으로 돈다.

되돌리기가 그대로 남는 항목은 `LastLogId == 0`인 것들뿐이다 — `BuildChangeSet`의 Git 폴백으로
들어온, 열린 로그 행이 없는 파일이다. 트리거 설치 이전의 변경, "전체 다시 추출"이 쏟아낸 파일,
인코딩 전환 산출물이 여기 속한다.

그래서 질문은 "행까지 닫아 주면 더 친절한가"가 아니다. **닫지 않으면 버튼이 눈에 보이게
아무 일도 하지 않는 경우가 있다.**

### 1.2 그러나 행을 닫는 것은 공유 DB에서 전역이다

`DBVC_ChangeLog`는 한 DB에 하나이고 개발자 20명 + DBA 3명이 그 DB를 함께 쓴다. 내가 닫은 행은
**모두의** 목록에서 사라진다. 그런데 DB의 변경은 그대로 남아 있으므로, 그 변경은 앞으로 어떤
새로고침에도 다시 오르지 않고 git에 영영 담기지 않는다.

이것은 백로그 5번(미채택자 행 누적)이 말하는 실패를 자기 손으로 만드는 것이다. 되돌리기 버튼
하나에 그 부작용을 숨기면, 벽을 넘으라고 만든 버튼이 개발자 20명에게 가장 위험한 버튼이 된다.

### 1.3 행을 닫는 길은 이미 있다

되돌려서 파일이 저장소와 같아진 항목을 커밋하면 `CommitChanges`가 `NothingToCommit`을 내고,
화면은 그때도 `MarkProcessed`를 부른다(`ViewChangesViewModel.cs:1747`의 주석이 정확히 이 경우를
위해 쓰여 있다). 사용자에게는 "선택한 항목은 저장소와 이미 같아 커밋할 것이 없었습니다.
목록에서만 정리했습니다."가 뜬다(`:1810`).

로그 행을 닫는 자리는 지금도 커밋 하나뿐이고, 그 자리에는 CoAuthor 확인과 신원 검사가 이미
걸려 있다. 되돌리기가 두 번째 자리를 만들 이유가 없다.

## 2. 결정

### 2.1 되돌리기는 파일까지만 한다

로그 행은 건드리지 않는다. 공유 `DBVC_ChangeLog`에 대한 영향이 0이고, 영구히 치우는 길은
1.3의 것을 쓴다 — 되돌린 뒤 커밋하면 행이 닫힌다.

대가는 1.1의 자기 취소다. 그것을 숨기지 않고 **확인 문구에 명시한다**:
"데이터베이스의 변경은 그대로 남습니다. 다음 새로고침에서 다시 추출됩니다."
이 문장이 없으면 사람들은 되돌리기를 "DB 변경 취소"로 읽는다.

행을 닫는 동작은 이름부터 다르다 — "되돌리기"가 아니라 "무시"다. 보존 정책·미채택자 행과 함께
설계되어야 하므로 백로그 4·5번으로 남긴다.

### 2.2 미추적 파일도 같은 버튼에 묶는다

세 갈래가 이렇게 갈린다.

| 상태 | 되돌리기의 실제 동작 | 복구 |
| --- | --- | --- |
| 수정 | `HEAD` 내용으로 덮어쓴다 | git이 갖고 있다 |
| 삭제 | `HEAD`에서 파일을 되살린다 | git이 갖고 있다 |
| **추가** | **파일을 지운다** | git에 없다 |

`추가`만 성격이 다르지만, 비가역성의 크기는 겉보기보다 작다. 저장소의 `.sql`은 SMO가 만든
것이라 객체가 DB에 살아 있는 한 "전체 다시 추출"로 돌아온다. 잃는 것은 손으로 고친 내용뿐이고,
이 저장소에서 그것은 정상 작업 방식이 아니다.

**나누는 것은 버튼이 아니라 확인 문구다**(3.6). 버튼을 둘로 쪼개면 선택 모델은 하나인데 동작이
둘이 되어, 어느 버튼이 어느 항목에 적용되는지가 화면에서 사라진다. 미추적만 기본 제외하는
변형도 쓰지 않는다 — 체크한 것이 조용히 빠지면 사용자는 "지웠는데 남아 있다"로 읽는다.

`삭제` 상태도 되돌리기에 포함한다(파일을 되살린다). DROP 로그 행이 열려 있으면 다음 새로고침의
`WorkingTreeCleaner`가 다시 지우는데, 2.1이 받아들인 자기 취소와 같은 성질이고 같은 문구로 덮인다.

### 2.3 Write 클론에만 연다

`PanelSelector.Select`가 `mode != Write`를 전부 `DeploymentPanel`로 보내므로
(`PanelSelector.cs:22`), 배포·감사 클론에는 체크박스 목록도 되돌리기 버튼도 렌더링되지 않는다.

**막다른 곳이 거기 있다는 것은 알고 남긴다.** 배포·감사 클론은 더러운 트리면
`RepositoryStateEvaluator.DeniesDirtyWorkingTree`가 `WorkingTreeDirty`로 차단하고
(`RepositoryStateEvaluator.cs:58`), `IsBlocked`는 모든 버튼을 끈다 — 도구 안에 빠져나갈 길이 없다.

그럼에도 열지 않는 이유는, 배포·감사 클론을 DBVC가 더럽힐 수 없기 때문이다. `Extract`는 금지되어
있고 `CompareWithRepository`는 아무것도 쓰지 않는다. 거기가 더럽다는 것은 **DBVC 밖의 무언가가
만졌다**는 뜻이고, 그것을 DBVC가 말없이 치우는 것은 "DBVC는 저장소의 유일한 주인이 아니다"가
경계하는 바로 그 동작이다. 별도 항목으로 백로그에 적는다(6장).

`IsBlocked`인 Write 클론에서도 끈다. 병합 중에 경로별로 되돌리면 병합 상태가 더 헝클리고,
브랜치가 틀린 트리에서 되돌리는 것은 틀린 기준으로 덮어쓰는 일이다. `CanCommit`과 같은 판단이다.

## 3. 설계

### 3.1 `DiscardPlan` — 판정의 유일한 자리

Core에 순수 함수로 둔다. Git도 DB도 닿지 않으므로 판정의 시험대가 여기 하나다.

```
DiscardPlan DiscardPlan.Build(
    IEnumerable<string> requestedPaths,
    IReadOnlyDictionary<string, string> gitStates)   // GetChangedFileStates의 결과

  RestorePaths  // 수정·삭제 → HEAD 내용으로
  DeletePaths   // 추가 → 파일 삭제
  SkippedPaths  // 대상 아님
```

화면은 이 함수로 확인 문구를 만들고, `GitManager.DiscardChanges`는 같은 함수로 실행 대상을
정한다. `MappingPolicy`를 `CanExecute`와 Core 진입부가 함께 부르는 구조를 그대로 따른다 —
판정이 두 곳에 생기면 갈라지고, 갈라지는 날 "지울 파일 0개"라고 말해 놓고 지운다.

제외 규칙 셋은 전부 `WorkingTreeCleaner.ResolveDeletableFile`의 선례를 따른다.

1. **지금 Git이 변경으로 보고하지 않는 경로.** 화면 목록은 사용자가 10분 들여다본 만큼 낡을 수
   있고, 그 사이에 깨끗해진 파일을 `HEAD`로 덮어쓰는 것은 되돌리기가 아니라 손실이다.
2. **`ObjectPathConvention.TryParseRelativePath`가 실패하는 경로.** DBVC가 만든 파일이 아니다.
   `README`·`.gitattributes`·추출 기준선 표식이 이 검사로 보호된다.
3. **저장소 루트 밖으로 풀리는 경로.** `..` 세 조각은 1·2를 통과할 수 있어 루트 검사가 마지막
   방어선이다.

`IGitManager`에는 넣지 않는다. `MappingPolicy`·`RepositoryEncoding`·`GitIdentity`가 정적 호출로
쓰이는 선례를 따른다 — 인터페이스에 얹으면 대역 구현이 함께 늘어나는데 얻는 것이 없다.

### 3.2 `IGitManager.DiscardChanges`

`CommitChanges`와 같은 모양이다.

```
DiscardResult DiscardChanges(string serverName, string databaseName, IEnumerable<string> relativePaths)
```

진입부 순서도 `CommitChanges`와 같다: `ResolveRepoPath` → `MappingPolicy` 검사 → 저장소 열기.
매핑이 없으면 아무것도 하지 않고 빈 결과를 낸다.

**Core는 요청받은 경로를 그대로 믿지 않는다.** 저장소를 연 뒤 스스로 `GetChangedFileStates`를
읽어 `DiscardPlan.Build`에 넣고, 그 결과만 처리한다. 화면이 넘긴 목록은 낡을 수 있다(3.1의 1번).

실행은 둘로 갈린다.

- **되돌림:** `IRepository.CheckoutPaths("HEAD", paths, options)` — `CheckoutOptions`를 받는
  3-인자 오버로드를 직접 부른다. **`RepositoryExtensions.CheckoutPaths(repo, "HEAD", paths)`의
  2-인자 형태는 확장 메서드일 뿐이고, 내부에서 `options`에 `null`을 넘겨
  `CheckoutModifiers.None`(Safe 모드)으로 동작한다.** Safe 모드는 작업 트리에서 이미 수정된
  파일을 덮어쓰지 않고 조용히 건너뛴다 — "인덱스와 작업 트리를 함께 갱신한다"는 API 문서의
  설명은 파일이 깨끗할 때만 성립했다. 되돌리기의 목적 자체가 그 수정을 지우는 것이므로 2-인자
  형태로는 아무 일도 일어나지 않는다(구현 중 실측으로 걸렸다). 그래서
  `CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force }`를 명시적으로 넘기는
  3-인자 오버로드를 쓴다. 이 오버로드로도 별도의 `Index.Replace`는 필요 없다 — Force로 불러도
  인덱스는 함께 갱신되므로, 외부 클라이언트가 스테이징해 둔 상태도 같이 풀린다.

  **`paths`는 리터럴 경로가 아니라 libgit2의 wildmatch 패스스펙이다.** `[`·`]`는 문자 클래스,
  `*`·`?`는 와일드카드로 해석된다. SQL 구분 식별자는 대괄호를 허용하고 그대로 Windows 파일명이
  될 수 있어(`Users[1]` → `dbo/Tables/Users[1].sql`), 그런 경로는 자기 자신과 매치되지 않으면서
  `Users1.sql` 같은 요청하지 않은 파일을 대신 덮어쓸 수 있다 — 되돌리기가 막으려는 바로 그
  데이터 손실이다. `LibGit2Sharp`의 `CheckoutModifiers`는 `None`/`Force`뿐이라 이 매칭을 끄는
  libgit2의 `GIT_CHECKOUT_DISABLE_PATHSPEC_MATCH`를 세울 방법이 없다. 그래서 이런 문자가 섞인
  경로는 되돌리기 대상에서 제외하고 실패로 보고한다. `*`·`?`는 Windows 파일명에 올 수 없으므로
  이 검사는 실제 파일명에 비용이 없다.
- **삭제:** `File.Delete`(리터럴 경로라 대괄호가 있어도 안전하다). `Repository.RemoveUntrackedFiles()`는
  경로 필터가 없어(저장소 전체) 쓸 수 없다. 인덱스에 올라간 미추적 파일은 `Commands.Unstage`로
  먼저 내리는데, 이 API의 경로도 `CheckoutPaths`와 같은 wildmatch 패스스펙이라 같은 문자 제한이
  적용된다 — 대괄호가 섞인 경로는 인덱스 항목이 있어도 안전하게 내릴 방법이 없으므로 파일을
  지우지 않고 실패로 보고한다.

**파일 하나씩 `try/catch` 한다.** SSMS 편집기가 `.sql`을 열어 둔 잠금이 현실적인 실패이고,
하나가 막혔다고 나머지가 멈추면 안 된다(`WorkingTreeCleaner`·`SmoManager.ScriptAll`과 같은 방침).
`CheckoutPaths`는 경로 목록을 받지만 **경로 하나짜리 목록으로 한 번씩 부른다** — 한 배치로
부르면 잠긴 파일 하나가 전부를 무너뜨려, 실패한 경로를 가려낼 수 없다. 되돌리기 한 번의 대상은
많아야 수백 개(`MaxChangedFilesPerCommit`이 500)이므로 저장소를 한 번만 열면 비용이 문제되지 않는다.

**커밋이 하나도 없는 저장소에서는 되돌림이 성립하지 않는다.** "이미 받아둔 폴더를 연결" 갈래로
빈 저장소가 들어올 수 있다. 그때 `RestorePaths`는 전부 `FailedPaths`로 넘긴다 — 조용히 건너뛰면
사용자는 되돌아간 줄 안다. 삭제는 기준이 필요 없으므로 그대로 진행한다.

`DiscardResult`는 `CleanupResult`의 모양을 따른다:
`RestoredPaths` / `DeletedPaths` / `FailedPaths` / `SkippedPaths`.

### 3.3 `MappingPolicy`

`DbvcOperation.Discard`를 더하고 `mode == MappingMode.Write`만 허용한다. 한국어 이름은
"작업 트리 되돌리기"다. 표의 `default`가 예외를 던지므로 새 동작을 빠뜨릴 수 없다.

### 3.4 화면 — 버튼과 재진입

커밋 버튼 옆에 `되돌리기`. `CanDiscard`는 `CanCommit`에서 커밋 메시지 조건만 뺀 것이다.

```
!IsBlocked && HasContext && IsMapped && IsInitialized && !IsBusy
  && Changes.Any(c => c.IsSelected)
  && MappingPolicy.IsAllowed(Mode, DbvcOperation.Discard)
```

`RaiseActionCanExecuteChanged`에 추가한다 — 이 뷰모델은 `CommandManager.RequerySuggested`를
구독하지 않으므로 여기서 직접 올리지 않으면 버튼이 낡은 채로 남는다.

흐름은 `Commit(coAuthorConfirmed)`의 재진입 패턴을 그대로 쓴다. 판정이 저장소를 여는 일이라 UI
스레드에서 할 수 없고, 확인은 UI 스레드에서만 띄울 수 있다.

```
Discard() → Discard(confirmed: false)

Discard(confirmed)
  백그라운드: gitStates = GetChangedFileStates
              confirmed면 → DiscardChanges 실행 → Outcome{Result}
              아니면      → DiscardPlan.Build   → Outcome{Plan}
  UI:  Plan이면   → 비었으면 안내하고 끝 / 아니면 확인 → Discard(confirmed: true)
       Result면   → 목록 갱신 → 결과 알림
```

무한 반복 가드는 두지 않는다. `confirmed`가 참인 경로는 대화상자를 띄우지 않으므로 두 번째
왕복이 없다 — 신원 검사에 `identityPrompted`가 필요했던 이유(입력받고도 판정이 다시 "없음"으로
돌아옴)가 여기엔 없다.

### 3.5 재추출 없는 갱신

**되돌린 뒤에 지금의 `Refresh()`를 부르면 같은 클릭 안에서 되돌리기가 취소된다**(1.1).

규약을 하나로 못 박는다: **재추출 없는 갱신은 저장소에 아무것도 쓰지 않는다.** `Extract`뿐 아니라
`WorkingTreeCleaner`도 건너뛴다. 되돌리기가 `삭제` 항목의 파일을 되살린 직후 같은 갱신 안에서
cleaner가 그것을 다시 지우면, 사용자가 누른 일이 눈앞에서 취소된다.

`Refresh(fullExtraction, reloadHistory)`에 `syncRepository` 인자를 더해 `GatherRefresh`까지
내린다. 이력은 다시 읽지 않는다 — 커밋이 만들어지지 않았다.

`_failedCleanupPaths`는 `Refresh` 진입부에서 지금처럼 비운다. 작업 트리가 방금 바뀌었으므로
이전의 정리 실패 표시는 낡은 정보다.

### 3.6 문구

확인은 `_notifier.Confirm`을 쓴다. `MessageBoxNotifier.Confirm`은 이미 Warning 아이콘에 기본
선택이 `Cancel`이라 무심코 Enter로 통과되지 않는다.

```
선택한 변경을 저장소의 마지막 커밋 내용으로 되돌립니다.

  되돌릴 파일 3개
  지울 파일 2개 — 복구되지 않습니다
    · dbo/Tables/Orders.sql
    · dbo/StoredProcedures/usp_Sync.sql

데이터베이스의 변경은 그대로 남습니다. 다음 새로고침에서 다시 추출됩니다.

계속할까요?
```

- **지울 파일만 이름을 나열한다.** 되돌릴 파일은 git이 갖고 있으므로 셈만으로 충분하고, 사람이
  읽어야 하는 것은 복구되지 않는 쪽이다. **20개까지 적고** 넘으면 "외 N개"로 접는다 — 전체 다시
  추출 뒤라면 수백 개가 될 수 있고, 화면 밖으로 넘친 목록은 확인이 아니라 장애물이다.
- 마지막 문단이 2.1이 받아들인 자기 취소를 말한다.

결과 알림은 커밋의 선례를 따른다.

- **실패가 있으면 상자로**(`ShowError`, 제목 "DBVC 되돌리기 — 일부 실패"). 잠긴 파일이 현실적인
  실패이므로 "다른 프로그램이 파일을 열고 있는지 확인하세요"를 붙인다. `WarningMessage`에 담으면
  뒤이은 갱신의 `ApplyRefreshOutcome`이 덮어써 사라진다 — 커밋 쪽에 이미 기록된 함정이다.
- **제외된 항목이 있으면 요약 문구에 담는다.** 체크한 것이 조용히 빠지면 안 된다.
- 실패도 제외도 없으면 갱신 **뒤에** `WarningMessage`에
  "되돌렸습니다 — 되돌림 3개, 삭제 2개."

## 4. 검증

| 확인할 것 | 어디서 |
| --- | --- |
| 수정·삭제는 되돌림, 추가는 삭제로 갈린다 | `DiscardPlanTests` |
| **Git이 지금 변경으로 보고하지 않는 경로는 제외된다** | `DiscardPlanTests` |
| 규약 밖 경로(`README.md`·`.gitattributes`)는 제외된다 | `DiscardPlanTests` |
| `..`이 섞인 경로는 제외된다 | `DiscardPlanTests` |
| 수정된 파일이 HEAD 내용으로 돌아온다 | `GitManagerTests` |
| 미추적 파일이 지워진다 | `GitManagerTests` |
| 삭제된 파일이 되살아난다 | `GitManagerTests` |
| **스테이징된 변경도 함께 되돌아간다** | `GitManagerTests` |
| **요청했지만 지금 깨끗한 파일은 건드리지 않는다** | `GitManagerTests` |
| 커밋이 없는 저장소에서 되돌림이 실패로 기록된다(조용히 넘어가지 않는다) | `GitManagerTests` |
| 배포·감사 mode에서 `OperationNotAllowedException` | `GitManagerTests` · `MappingPolicyTests` |
| 확인을 취소하면 아무것도 되돌아가지 않는다 | `ViewChangesViewModelTests` |
| **되돌린 뒤 갱신이 재추출하지 않는다** | `ViewChangesViewModelTests` |
| **되돌린 뒤 갱신이 `WorkingTreeCleaner`를 부르지 않는다** | `ViewChangesViewModelTests` |
| 확인 문구에 지울 파일 이름과 "복구되지 않습니다"가 들어간다 | `ViewChangesViewModelTests` |
| 실패한 경로가 상자로 알려진다 | `ViewChangesViewModelTests` |
| `IsBlocked`·비-Write에서 버튼이 꺼진다 | `ViewChangesViewModelTests` |

굵은 넷이 "왜 이 설계인가"를 지키는 테스트다. 재추출·cleaner 단언이 없으면 다음 사람이 갱신을
그냥 `Refresh()`로 바꿔 기능이 조용히 무력해진다 — 버튼은 그대로 있는데 눌러도 아무 일이 없어진다.
깨끗한 파일 단언이 없으면 낡은 목록 방어가 사라지고, 스테이징 단언이 없으면 `CheckoutPaths`가
인덱스까지 본다는 근거가 코드에서 사라진다.

### 4.1 CI가 검증하지 못하는 것

대화상자 렌더링, 버튼 배치, 실제 파일 잠금. `docs/setup-checklist.md`에 절차를 적고 SSMS 21에서
직접 확인한다.

1. 객체를 하나 바꿔 새로고침 → 되돌리기 → 파일이 저장소 내용으로 돌아온다
2. **그 항목은 목록에 그대로 남아 있고, 새로고침을 누르면 다시 더러워진다** — 2.1이 받아들인
   자기 취소가 설계대로인지 눈으로 본다
3. 새 객체를 만들어 추출 → `추가` 항목 → 확인 문구에 파일 이름과 "복구되지 않습니다"가 뜬다 →
   파일이 사라진다
4. 되돌릴 `.sql`을 편집기로 열어 둔 채 되돌리기 → 실패 상자가 뜨고 나머지는 처리된다
5. 배포 클론에는 되돌리기가 보이지 않는다(변경 목록 패널 자체가 없다)

## 5. 범위 밖

- **`DBVC_ChangeLog`의 행을 닫지 않는다**(2.1). 행을 닫는 "무시"는 백로그 4·5번과 함께 설계한다
- **배포·감사 클론에 UI를 열지 않는다**(2.3). 차단된 배포 클론의 막다른 길은 별도 항목이다
- **`IsBlocked`에서는 꺼진다**(2.3)
- **`reset --hard`·`stash`·브랜치 전환은 넣지 않는다.** 되돌리기는 경로 단위이고 커밋 이력을
  건드리지 않는다. 브랜치 전환은 백로그 3번이 정책으로 닫기를 권한 항목이다
- **되돌리기의 되돌리기는 없다.** 확인 대화상자가 그 자리를 대신한다
- **병합 충돌 해결은 여전히 도구 밖이다**(백로그 6번)

## 6. 릴리스

`0.5.18`. 스키마 버전은 바뀌지 않는다 — 데이터베이스를 건드리지 않는 변경이다.

`README.md`·`docs/setup-checklist.md`·`src/DBVC.Vsix/source.extension.vsixmanifest`를 함께 고친다.

`docs/team-rollout-backlog.md`의 2번을 완료로 옮기면서 **남은 두 조각을 명시적으로 적는다**:

- 행을 닫는 "무시"는 4·5번과 한 몸으로 설계한다
- 차단된 배포·감사 클론이 도구 안에서 빠져나올 수 없다는 것을 새 항목으로 세운다.
  변경 목록이 없는 화면이므로 차단 배너 위의 별도 버튼이 필요하다

배포 순서에 조건이 없다. 저장소를 공유하는 변경이 아니므로 각자 올리면 된다.
