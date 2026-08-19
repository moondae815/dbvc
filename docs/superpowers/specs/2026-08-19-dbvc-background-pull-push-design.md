# Pull·Push를 UI 스레드 밖으로 내보내는 설계

## 1. 문제

새로고침·연결·커밋은 `IBackgroundScheduler`를 타는데 **Pull과 Push만 UI 스레드에서 그대로 돈다.**
`ViewChangesViewModel.Pull()`이 `_gitManager.PullChanges`를, `Push()`가 `_gitManager.PushChanges`를
직접 호출한다.

이 둘은 네트워크와 `ssh` 프로세스가 걸리는 구간이다. 원격이 느리거나 응답하지 않으면 그동안
SSMS 전체가 멈춘다 — 도구 창뿐 아니라 메뉴·쿼리 편집기·개체 탐색기까지. `IBackgroundScheduler`가
존재하는 이유가 정확히 그것인데 이 두 경로만 빠져 있었다.

### 1.1 함께 드러난 것

- **`CanPull`·`CanPush`에 `!IsBusy`가 없다.** 추출이 도는 중에도 Pull이 눌린다. libgit2 병합과
  SMO 추출이 같은 작업 트리를 동시에 건드린다.
- **취소 버튼이 취소할 수 없는 작업에도 뜬다.** `CancelCommand`의 조건이 `IsBusy` 하나뿐이라
  연결·커밋 중에도 나타나는데, `Cancel()`이 취소하는 것은 추출용 `CancellationTokenSource`다.
  눌러도 아무 일이 없고 "취소하는 중..."만 뜬다. Pull·Push가 `IsBusy`를 세우면 이 결함이
  더 자주 보인다.

## 2. 결정

### 2.1 무엇을 백그라운드로 보내나

**`PullChanges`/`PushChanges` 호출만** 보낸다. 그 앞뒤는 UI 스레드에 남는다.

- **앞:** `CanPull` 판정, 매핑 조회, `GetChangedFiles`로 센 미커밋 변경 개수, 그리고 확인 대화상자.
  확인은 대화상자라 UI 스레드여야 하고, 사용자가 취소하면 백그라운드로 나갈 일 자체가 없다.
  `GetChangedFiles`는 git status 한 번이며 이 저장소는 그 비용을 이미 18ms 수준으로 줄여 두었다
  (`SmoManager.ScriptAll` 주석).
- **뒤:** 결과 분기, 알림, `History.Load`, `SelectionChanged`, `RaiseActionCanExecuteChanged`.
  전부 바인딩 대상을 건드리므로 완료 콜백에서 한다.

### 2.2 예외 처리

지금 Pull은 `MergeConflictException`·`WorkingTreeConflictException`을 "DBVC Pull 중단"으로,
나머지를 "DBVC Pull 실패"로 가른다. 스케줄러의 실패 콜백은 `Exception` 하나를 받으므로 그 안에서
타입으로 가른다. `VsBackgroundScheduler`는 `await Task.Run(work)`이라 원래 예외가 그대로 오고,
`InlineBackgroundScheduler`도 같다. 문구와 갈래는 그대로 유지한다.

### 2.3 진행 표시와 재진입

- `IsBusy`를 세우고 `ProgressText`에 무엇을 하는 중인지 적는다. 성공·실패 어느 쪽으로 끝나도
  반드시 내려놓는다.
- `CanPull`·`CanPush`에 `!IsBusy`를 더한다. 작업 중 재진입을 막는다.

### 2.4 취소 버튼

`CancelCommand`의 조건을 "취소할 수 있는 작업이 진행 중일 때"로 좁힌다. 추출만 취소 가능하므로
그 사실을 값으로 들고 있는다. Pull·Push·연결·커밋 중에는 버튼이 뜨지 않는다.

취소 가능한 Pull은 만들지 않는다. libgit2의 병합은 중간에 끊으면 작업 트리를 어중간한 상태로
남길 수 있고, `PullChanges`는 지금 취소 토큰을 받지도 않는다. 없는 취소를 있는 척하는 버튼보다
버튼이 없는 편이 정직하다.

## 3. 검증

- Pull·Push가 스케줄러로 넘어가는지, 호출자 스레드에서 `PullChanges`/`PushChanges`가 불리지
  않는지.
- 작업이 걸려 있는 동안 `IsBusy`가 true이고 두 명령이 잠기는지.
- 백그라운드가 던진 예외가 갈래별 문구로 알려지고 `IsBusy`가 풀리는지.
- 취소 버튼이 추출 중에만 뜨는지.
- 기존 Pull·Push 테스트(결과 분기·확인 대화상자·알림 문구)는 그대로 통과해야 한다. 인라인
  스케줄러가 그 자리에서 실행하므로 손댈 이유가 없다 — 손대야 한다면 동작이 바뀐 것이다.
