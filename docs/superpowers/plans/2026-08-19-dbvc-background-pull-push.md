# Pull·Push 백그라운드화 구현 계획

설계: `docs/superpowers/specs/2026-08-19-dbvc-background-pull-push-design.md`

## 1. Pull

- [x] 실패하는 테스트: `Pull_HandsTheNetworkWorkToTheScheduler` — 명령 실행 직후에는
      `PullChanges`가 불리지 않고, `RunPending` 뒤에 한 번 불린다.
- [x] 실패하는 테스트: `IsBusy_IsTrueWhilePullWorkIsOutstanding`
- [x] 실패하는 테스트: `PullCommand_CannotExecute_WhileWorkIsOutstanding`
- [x] 실패하는 테스트: `Pull_ReleasesBusyAndReportsError_WhenBackgroundWorkThrows` —
      "DBVC Pull 실패" 갈래
- [x] 실패하는 테스트: `Pull_KeepsTheInterruptionWording_WhenTheBackgroundMergeConflicts` —
      `MergeConflictException`이 "DBVC Pull 중단"으로 남는지
- [x] `Pull()`을 확인 대화상자까지의 앞부분 / `_scheduler.Run` / 완료 콜백으로 나눈다.
- [x] `CanPull`에 `!IsBusy`를 더한다.
- [x] 통과 확인.

## 2. Push

- [x] 실패하는 테스트: `Push_HandsTheNetworkWorkToTheScheduler`
- [x] 실패하는 테스트: `PushCommand_CannotExecute_WhileWorkIsOutstanding`
- [x] 실패하는 테스트: `Push_ReleasesBusyAndReportsError_WhenBackgroundWorkThrows`
- [x] `Push()`를 같은 모양으로 나눈다. `CanPush`에 `!IsBusy`를 더한다.
- [x] 통과 확인.

## 3. 취소 버튼 조건 좁히기

- [x] 실패하는 테스트: `CancelCommand_IsUnavailable_WhenTheOutstandingWorkCannotBeCancelled` —
      Pull이 걸려 있는 동안 취소 버튼이 뜨지 않는지.
- [x] 취소 가능 여부를 값으로 들고, `CancelCommand`의 조건에 더한다. 추출 경로에서만 세운다.
- [x] 기존 `CancelCommand_IsOnlyAvailableWhileWorkIsOutstanding`이 그대로 통과하는지 확인한다.

## 4. 문서·버전

- [x] `README.md` — Pull·Push 항목에 진행 중 SSMS를 계속 쓸 수 있다는 것을 적는다.
- [x] `docs/setup-checklist.md` 동작 검증에 항목 추가.
- [x] `source.extension.vsixmanifest` 버전 올림.

## 5. 마무리

- [x] `dotnet build DBVC.slnx`, Core·Vsix 테스트 전부.
- [x] 기존 Pull·Push 테스트를 하나도 고치지 않았는지 확인한다. 고쳤다면 동작이 바뀐 것이다.
- [x] SSMS 21에서 직접 확인할 항목을 사용자에게 알린다.
