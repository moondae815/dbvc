# DBVC Git Push 설계

## 1. Overview

DBVC는 지금 커밋까지만 한다. 원격에 올리는 것은 사용자가 Git 클라이언트에서 직접
`git push` 를 실행해야 한다(`docs/setup-checklist.md` 의 "Push 기능이 없다").

이것은 기술적 제약이 아니라 **범위 결정**이었다. 이 문서는 그 결정을 뒤집고 Push를
DBVC 안으로 들이는 설계를 다룬다.

동기는 운영 워크플로다. 도입 체크리스트 5단계는 `Setup DBVC → Refresh → Commit →
(Git 클라이언트로 나가서) push → Pull` 을 요구한다. 스키마 변경을 SSMS 안에서 끝내는
것이 DBVC의 목적인데, 원격 반영만 창 밖으로 나가야 하는 상태다.

## 2. 측정된 전제

추측이 아니라 이 저장소와 설치된 패키지에서 직접 확인한 사실이다.

* `LibGit2Sharp 0.32.0`(이 프로젝트가 고정한 버전)은 `Network.Push(Branch, PushOptions)`
  오버로드를 제공한다. 새 패키지 참조가 필요 없다.
* `PushOptions` 는 `CredentialsProvider`, `CertificateCheck`, `ProxyOptions`,
  `OnPushStatusError`, `OnNegotiationCompletedBeforePush` 등을 갖는다.
  `FetchOptions` 와 인증 관련 표면이 같다.
* `PushStatusError` 는 `Reference` 와 `Message` 두 속성뿐이다. 오류 코드는 없다.
* `NonFastForwardException` 이 존재하며 요약은 "The exception that is thrown when push
  cannot be performed against the remote without losing commits"다. `LibGit2SharpException`
  파생이므로 catch 순서가 정확성 문제가 된다(4.3).
* **SSH 인증은 Pull과 완전히 동일하다.** libgit2가 SSH를 시스템 `ssh` 실행 파일에
  위임한다는 사실은 [2026-08-03-dbvc-ssh-first-git-auth-design.md](2026-08-03-dbvc-ssh-first-git-auth-design.md)
  2절에서 이미 측정됐고, 이는 전송 계층의 성질이므로 fetch/push를 가리지 않는다.
  따라서 `RemoteDiagnostics` 와 `SshExecutableLocator` 를 그대로 재사용할 수 있다.
* 앞선 설계 문서 32행의 "Push·Clone·Fetch API가 없다"는 **당시 코드 상태의 서술**이지
  불가 판정이 아니다. 이 설계가 그중 Push를 없앤다.

## 3. Scope

### In Scope

* `IGitManager.PushChanges` 와 `GitManager` 구현
* 서버의 ref 갱신 거부(`OnPushStatusError`)를 예외로 승격
* View Changes 도구 창의 Push 버튼과 `PushCommand`
* `PullChanges` 와 중복되는 원격·추적·진단 계산을 공유 헬퍼로 추출
* README·도입 체크리스트에서 "Push 없음" 서술 정정

### Out of Scope

* **Force push.** 원격 이력을 덮어쓰는 연산이다. 버튼 하나로 노출할 물건이 아니다.
* **추적 브랜치 자동 설정(`git push -u` 상당).** 아래 4.2 참조.
* **태그 push, 현재 브랜치가 아닌 브랜치 push, 원격 선택 UI.** 현재 워크플로에 없다.
* **진행률 표시(`OnPushTransferProgress`).** `.sql` 텍스트 파일이며 전송량이 작다.
* **non-fast-forward 확정 판정.** 4.3의 "명시적 연기" 참조.

## 4. Component Design

### 4.1. `PushResult` (신규, `DBVC.Core.Models`)

```csharp
public enum PushResult
{
    NoMapping,
    NothingToPush,
    Pushed
}
```

`PullChanges` 는 `bool` 을 돌려주지만 Push는 결과가 셋이다. "올릴 커밋이 없다"를
`false` 로 묶으면 호출자가 정상 상태와 매핑 실패를 구분하지 못하고, 사용자에게
정상 상태를 오류로 보고하게 된다. 저장소에 `CleanupResult`·`ScriptResult` 전례가 있다.

### 4.2. `GitManager.PushChanges`

```csharp
PushResult PushChanges(string serverName, string databaseName);
```

순서:

1. 매핑 해석. 없으면 `PushResult.NoMapping`.
2. 원격 없음 → `InvalidOperationException`.
3. `repo.Head.IsTracking == false` → `InvalidOperationException` +
   `git push -u origin <브랜치>` 안내.
4. `RemoteDiagnostics.Explain(remoteUrl, sshAvailable)` 로 안내를 미리 계산.
5. `repo.Head.TrackingDetails.AheadBy == 0` → `PushResult.NothingToPush`.
6. `repo.Network.Push(repo.Head, options)`.
7. `NonFastForwardException` 이 나오거나 `OnPushStatusError` 가 수집한 것이 있으면
   `GitPushRejectedException`(4.3).
8. 아니면 `PushResult.Pushed`.

**3단계에서 추적을 대신 설정하지 않는다.** `PullChanges` 가 같은 자리에서
"버튼 하나가 사용자의 git config를 조용히 바꾸면 안 된다"는 이유로 거부하고 안내만
하고 있으며(`GitManager.cs`), Push라고 해서 그 원칙이 달라지지 않는다. 도입
체크리스트는 `git init` 이 아니라 clone을 권하므로 정상 경로에서는 나오지 않는 상태다.

**5단계의 `AheadBy` 는 마지막 fetch 기준의 로컬 값이다.** 이 값이 0인데 실제로는
원격에 없는 커밋이 있는 상황은 만들 수 없다 — 로컬 커밋은 언제나 `AheadBy` 를
증가시킨다. 반대로 이 값이 0보다 큰데 원격이 이미 갖고 있는 경우는 있을 수 있고,
그때 push는 아무 일도 하지 않고 성공한다. 즉 **이 검사는 헛수고를 줄이는 것이지
정확성의 근거가 아니며**, 그래서 성공/거부 판정을 여기에 기대지 않는다.

**2~4단계는 `PullChanges` 의 같은 자리와 글자 그대로 같다.** 이 연속된 세 단계를
private 헬퍼(열린 `Repository` 를 받아 `guidance` 를 돌려주고, 2·3에서 예외를 던진다)로
뽑아 둘이 공유한다. Push를 위해 새 추상화를 만드는 것이 아니라, 그대로 두면 복제될
코드를 한 번만 두는 것이다.

**7단계의 catch 구조는 `PullChanges` 와 동일하다.** 맨 앞의 `NonFastForwardException`
만 다르며, 그 자리는 `PullChanges` 에서 `CheckoutConflictException` 이 차지한 자리다.

```
catch (NonFastForwardException ex)                                 // 반드시 첫 번째
    → GitPushRejectedException(4.3의 문구, ex)
catch (LibGit2SharpException ex) when (requiresUserCredentials)
    → GitAuthenticationException(guidance ?? CredentialFallbackMessage, ex)
catch (LibGit2SharpException ex) when (guidance != null)
    → GitRemoteException($"{ex.Message}{개행}{개행}{guidance}", ex)
```

`CheckoutConflictException` 에 해당하는 catch는 **없다.** Push는 작업 트리를
체크아웃하지 않는다. 같은 이유로 `MergeConflictException`·`AbortMerge` 도 없다.
Push 경로는 작업 트리·인덱스·로컬 브랜치 이력을 전혀 변경하지 않는다. 성공하면
원격 추적 ref(`refs/remotes/...`)만 갱신되고(`git_remote_update_tips`), 실패하면
그마저도 바뀌지 않는다 - 잃을 것이 없으므로 되돌릴 경로도 필요 없다.

### 4.3. 거부 처리와 `GitPushRejectedException` (신규)

**거부는 두 경로로 온다. 둘 다 잡아야 한다.**

| 경로 | 언제 | 신호 |
| --- | --- | --- |
| `NonFastForwardException` | libgit2가 스스로 판정할 때 (로컬/파일 전송) | 예외 |
| `OnPushStatusError` | 서버가 ref 갱신을 거부했을 때 (smart 전송 — SSH·HTTPS) | 콜백 |

`LibGit2Sharp 0.32.0`의 `NonFastForwardException` 요약은 "push cannot be performed
against the remote without losing commits"다. **`OnPushStatusError` 만 붙이면 로컬 전송
경로를, 예외만 잡으면 SSH 경로를 놓친다.** 특히 콜백을 붙이지 않은 채 두면
`Network.Push` 가 정상 반환하므로 **실패가 성공으로 보고된다.**

```csharp
var errors = new List<PushStatusError>();
options.OnPushStatusError = e => errors.Add(e);
```

`NonFastForwardException` 을 잡거나 `errors.Count > 0` 이면 `GitPushRejectedException`
을 던진다. `MergeConflictException`·`GitRemoteException` 과 같은 형태의 `Exception`
파생 타입이다.

**`catch (NonFastForwardException)` 은 반드시 `catch (LibGit2SharpException ...)` 보다
앞에 둔다.** 파생 타입이므로 순서가 곧 정확성이다 — 뒤로 밀면 SSH 원격에서 발생한
거부가 `GitRemoteException` 으로 둔갑한다. `PullChanges` 가 `CheckoutConflictException`
에서 같은 이유로 순서를 고정하고 주석으로 못 박아 둔 것과 같은 함정이다.

메시지는 서버 원문을 먼저 싣고, 그 아래 원인 후보를 붙인다.

```
원격이 '<Reference>' 갱신을 거부했습니다.
서버 응답: <Message>

원인은 보통 둘 중 하나입니다.
- 원격에 로컬로 가져오지 않은 커밋이 있습니다. Pull을 먼저 하세요.
- 이 브랜치가 보호되어 있거나 밀어넣을 권한이 없습니다.
```

**libgit2/서버 메시지를 문자열로 매칭해 분기하지 않는다.** 그 메시지가 버전과 전송
방식에 따라 달라진다는 것은 이 저장소에 이미 기록된 사실이다(`GitManager.ResolveCredentials`
주석). 원문을 그대로 보여주고 판정하지 않는다.

**원인 후보를 둘로 한정한 것이 "모든 실패에 힌트 덧붙이기"와 다른 이유.**
`RemoteDiagnostics.Explain` 이 SSH 원격을 확인한 뒤에만 확인 목록을 내놓는 것과
같은 절제다. 여기서는 **서버가 ref 갱신을 명시적으로 거부했음이 확인된 뒤에만**
나오고, force push를 제공하지 않는 이 설계에서 그 조건의 원인 후보는 실제로 그 둘뿐이다.

여러 ref가 거부되는 경우는 이 설계에서 발생하지 않는다 — 현재 브랜치 하나만 push한다.
그래도 `errors` 는 리스트로 받고 첫 항목을 메시지에 쓴다. 콜백 계약이 다중 호출을
허용하므로 단일 변수로 받으면 조용히 덮어쓰게 된다.

**명시적 연기 — non-fast-forward 확정 판정.** `OnNegotiationCompletedBeforePush` 가
주는 `PushUpdate` 의 `SourceObjectId`/`DestinationObjectId` 로 원격 tip과 로컬 HEAD의
조상 관계를 따지면 "원격이 앞서 있음"을 추측이 아니라 판정으로 말할 수 있다.
다만 두 필드 중 어느 쪽이 원격의 현재 값인지가 패키지 문서(`"The current target of
the reference"` / `"The new target for the reference"`)만으로는 확정되지 않는다.
**측정 없이 설계에 넣지 않는다.** 필요해지면 별도 작업으로 측정한 뒤 다룬다.

### 4.4. `ViewChangesViewModel.Push`

`PushCommand` 를 추가한다. `CanPush() => HasContext && IsMapped` — `CanPull` 과 같다.
`RaiseCanExecuteChanged` 를 모아 부르는 자리에 함께 등록한다.

본문은 `Pull` 보다 짧다.

* **사전 확인 대화상자가 없다.** `Pull` 의 확인은 병합이 미커밋 변경을 지울 수 있기
  때문인데, Push는 작업 트리·인덱스·로컬 브랜치 이력을 건드리지 않는다(4.2).
  사용자가 잃을 것이 없으므로 물을 것도 없다.
* **성공 후 `History.Load`·`SelectionChanged` 를 부르지 않는다.** 로컬에 바뀐 것이 없다.
  (`Pull` 은 새로 받은 커밋을 화면에 반영해야 해서 부른다.)
* `NoMapping` → `ShowError("DBVC Push 실패", "매핑된 Git 저장소를 찾을 수 없습니다.")`
* `NothingToPush` → `ShowInfo("DBVC Push", "올릴 커밋이 없습니다. 원격이 이미 최신입니다.")`
  — 오류가 아니다.
* `Pushed` → `ShowInfo("DBVC Push", "커밋을 원격 저장소에 올렸습니다.")`
* `catch (Exception ex)` → `ShowError("DBVC Push 실패", ex.Message)`

**`GitPushRejectedException` 전용 catch를 두지 않는다.** Core가 완전한 한국어 안내를
메시지에 담아 던지므로 전용 분기는 catch-all과 글자 그대로 같은 코드가 된다.
`Pull` 이 `GitAuthenticationException` 에서 실제로 겪고 제거한 결함이며, 그 주석이
"되살리지 말 것"으로 남아 있다.

### 4.5. UI 배치

`ViewChangesControl.xaml` 의 `WrapPanel` 에서 Pull 버튼 **오른쪽**에 넣는다.
Pull이 갖고 있는 오른쪽 여백(`Margin="0,0,16,4"` — 스크립트 버튼 그룹과의 구분)을
Push로 옮기고, Pull은 그룹 내부 간격(`0,0,10,4`)으로 바꾼다. Pull과 Push가 원격
연산으로 한 덩어리가 되고 스크립트 생성 버튼과의 구분선은 유지된다.

```xml
<Button Content="Pull" ... Margin="0,0,10,4" ... />
<Button Content="Push" Command="{Binding PushCommand}" Width="70" Margin="0,0,16,4"
        ToolTip="로컬 저장소의 커밋을 원격 저장소에 올립니다." />
```

## 5. Error Handling

| 상황 | 결과 |
| --- | --- |
| 매핑 없음 | `PushResult.NoMapping` → `ShowError` |
| 원격 미설정 | `InvalidOperationException` (Pull과 같은 문구 형태) |
| 추적 브랜치 없음 | `InvalidOperationException` + `git push -u origin <브랜치>` 안내 |
| 올릴 커밋 없음 | `PushResult.NothingToPush` → `ShowInfo` |
| HTTPS 원격 | `GitAuthenticationException` + SSH 전환 안내 (`RemoteDiagnostics` 재사용) |
| SSH 원격, `ssh` 실행 파일 없음 | `GitRemoteException` + OpenSSH 설치 안내 |
| SSH 원격, 그 밖의 통신 실패 | `GitRemoteException` + 원문 + SSH 확인 목록 |
| libgit2가 non-fast-forward로 판정 | `GitPushRejectedException` (`NonFastForwardException` 경로) |
| 서버가 ref 갱신 거부 | `GitPushRejectedException` + 서버 원문 + 원인 후보 2항목 |
| 로컬 경로 원격 등 | 안내를 덧붙이지 않는다. 원본 예외 전파 |

Push는 실패해도 **로컬 저장소를 변경하지 않는다.** Pull의 `AbortMerge` 에 해당하는
복구 경로가 필요 없는 이유다.

## 6. Testing Strategy

**단위 테스트 (네트워크 없음).** 기존 `GitManagerTests` 가 임시 폴더에 실제 저장소를
만들어 검증하는 방식을 그대로 쓴다. **파일 경로 원격(bare 저장소)** 으로 실제 push가
성립하므로 네트워크 없이 성공 경로까지 검증된다.

`GitManagerTests`

* 매핑 없음 → `NoMapping`
* 원격 미설정 → `InvalidOperationException`
* 추적 브랜치 없음 → `InvalidOperationException`, 메시지에 `git push -u origin <브랜치>` 포함
* 앞선 커밋 없음 → `NothingToPush`
* 로컬 bare 원격에 커밋 push → `Pushed`, **원격 저장소의 tip이 실제로 갱신됐는지 확인**
  (반환값만 보면 push가 아무것도 하지 않아도 통과한다)
* 원격을 먼저 앞서게 만든 뒤 push → `GitPushRejectedException`, 메시지에 원인 후보 2항목 포함
* 실패한 push 이후 로컬 HEAD가 그대로인지
* HTTPS 원격 → `GitAuthenticationException`, 메시지에 SSH 전환 안내 포함
* 로컬 경로 원격의 실패에는 안내가 붙지 않는지

`ViewChangesViewModelTests` (Moq)

* `NothingToPush` → `ShowInfo` 가 호출되고 `ShowError` 는 호출되지 않는지
* `NoMapping` → `ShowError`
* 예외 → `ShowError`, 메시지가 `ex.Message` 그대로인지
* 성공 시 `RefreshState`·`ScriptObjects` 가 **호출되지 않는지** (Push는 작업 트리를 건드리지 않으므로 다시 추출할 것이 없다)
* `CanPush` 가 `HasContext`·`IsMapped` 를 따르는지

**`PullChanges` 가 `[Explicit]` 로 밀려난 net48 무한 대기는 여기서 발생하지 않는다.**
그 문제는 HTTPS 원격에 실제로 접속을 시도할 때 생겼고, 위 테스트는 파일 경로 원격만
쓴다. HTTPS 원격 케이스는 접속 전에 판정되는 안내 문구만 확인한다.

**남는 공백(명시).** 파일 경로 원격은 거부를 `NonFastForwardException` 으로 낸다.
서버가 상태로 거부를 보고하는 `OnPushStatusError` 경로(SSH·HTTPS)는 **단위 테스트가
닿지 못한다.** 그 배선은 `BuildPushOptions` 가 콜백을 실제로 연결하는지를 검사하는
테스트(`BuildPullOptions` 테스트와 같은 형태)와 수동 검증으로만 지켜진다.
두 경로가 같은 `GitPushRejectedException` 으로 수렴하므로 사용자에게 보이는 결과는
같지만, 공백이 사라진 것은 아니다.

**수동 검증 (Windows/SSMS 21)**

* 개발 노트북: 커밋 후 Push, GitHub에 반영 확인
* 원격을 앞서게 만든 뒤 Push → 거부 안내가 뜨는지, 로컬이 그대로인지
* 올릴 것이 없는 상태에서 Push → 오류가 아니라 정보 안내인지
* 폐쇄망 PC: SSH로 GitLab에 Push

## 7. 기존 코드에 미치는 영향

* `IGitManager` 에 메서드 하나가 는다. `ViewChangesViewModelTests` 의 Moq 기반
  테스트는 인터페이스 목이므로 컴파일이 깨지지 않는다.
* `GitManager.PullChanges` 의 앞부분(원격 검사 → 추적 검사 → `guidance` 계산)이
  private 헬퍼로 이동한다. **동작은 바뀌지 않으며 기존 Pull 테스트가 그대로 통과해야 한다.**
* `RemoteDiagnostics`·`SshExecutableLocator`·`GitAuthenticationException`·
  `GitRemoteException` 은 **변경하지 않는다.** 호출자만 는다.
* `ViewChangesControl.xaml` 의 Pull 버튼 `Margin` 이 바뀐다.
* [2026-08-03-dbvc-ssh-first-git-auth-design.md](2026-08-03-dbvc-ssh-first-git-auth-design.md)
  2절의 "DBVC가 네트워크를 쓰는 지점은 Pull 하나뿐이다"가 더 이상 참이 아니다.
  해당 줄에 이 문서로 갱신됐다는 표기를 남긴다.
* `README.md` 기능 목록과 동작 방식, `docs/setup-checklist.md` 의 5단계·"알아둘 것"·
  문제 해결 표에서 "Push 기능이 없다"는 서술을 정정한다.
