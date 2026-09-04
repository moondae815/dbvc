# 커밋 작성자 신원 설계 — 20명이 같은 이름으로 커밋하는 것을 막는다

## 1. 문제

`GitManager.BuildSignature`가 신원 없는 저장소에서 고정 이름으로 떨어진다.

```csharp
// GitManager.cs:911
private static Signature BuildSignature(Repository repo)
{
    return repo.Config.BuildSignature(DateTimeOffset.Now)
        ?? new Signature(DefaultAuthorName, DefaultAuthorEmail, DateTimeOffset.Now);
}
```

`DefaultAuthorName = "DBVC User"`, `DefaultAuthorEmail = "dbvc@example.com"`이다.
`Configuration.BuildSignature(DateTimeOffset)`는 `user.name`·`user.email` 중 하나라도 없으면
`null`을 낸다(LibGit2Sharp 0.32.0 문서). 찾는 순서는 git과 같다 — 로컬 → 전역 → 시스템.

혼자 쓰는 동안에는 드러나지 않는다. 개발 PC에는 대개 전역 `git config`가 있어 폴백까지 가지
않기 때문이다. 드러나는 시점은 **전역 설정이 없는 사람이 팀에 들어오는 때**이고, 이 백로그가
전제하는 환경은 개발자 20명 + DBA 3명이다(`docs/team-rollout-backlog.md`).

### 1.1 폴백이 만든 커밋은 되돌릴 수 없다

`git blame`과 GitLab MR 작성자가 전부 `DBVC User`가 된다. 형상 관리를 도입한 이유의 절반이
여기서 사라진다 — 무엇이 바뀌었는지는 남지만 누가 바꿨는지는 남지 않는다.

고치려면 이력을 다시 써야 하고, 공용 저장소에서는 23명이 클론을 다시 받아야 한다.
**그래서 이것은 경고로 충분한 결함이 아니다.** 한 번 들어간 커밋은 사실상 영구적이다.

### 1.2 걸리는 자리가 둘이다

`BuildSignature`를 부르는 곳은 두 군데다.

| 위치 | 무엇을 만드나 |
| --- | --- |
| `GitManager.cs:333` — `CommitChanges` | 사용자의 커밋 |
| `GitManager.cs:356` — `PullChanges` | Pull의 **병합 커밋** |

Pull을 빠뜨리면 커밋만 막고 병합 커밋은 `DBVC User`로 새어 나간다. 배포·감사 클론도 Pull은
하므로, 커밋을 하지 않는 용도라고 해서 신원이 필요 없는 것이 아니다.

### 1.3 지금 테스트는 실행 기계의 전역 설정에 기대고 있다

테스트가 `CommitChanges`를 부르는 곳이 27군데, `PullChanges`가 44군데인데, 그중 커밋 작성자를
지정하는 곳은 **하나도 없다.** 폴백이 받아 주거나 개발 PC의 전역 설정이 받아 주거나 둘 중
하나인데, 어느 쪽인지 테스트만 봐서는 알 수 없다. 이것 자체가 결함이다 — 폴백을 없애는 이
작업이 그것을 드러낸다(4.2).

## 2. 결정

**폴백을 없앤다. 신원이 없으면 커밋과 Pull을 거부한다.** 대신 신원을 설정하는 길을 도구 안에
둔다 — 배너로 미리 알리고, 그래도 눌렀으면 막은 자리에서 입력을 받아 이어서 진행한다.

### 2.1 경고가 아니라 차단인 이유

배너만 두면 무시할 수 있고, 무시한 결과는 1.1대로 되돌릴 수 없다. 차단은 나쁜 커밋이 한 건도
생기지 않는 유일한 방법이다.

차단이 정당하려면 빠져나갈 길이 도구 안에 있어야 한다. 그래서 차단과 입력 다이얼로그를 한
동작으로 묶는다. **막다른 차단은 사람들이 도구를 쓰지 않게 만든다**(설계 3.10의 CoAuthor 확인이
차단이 아니라 확인인 것과 같은 판단이다. 다만 그쪽은 되돌릴 수 있는 일이고 이쪽은 아니다).

### 2.2 저장 위치는 저장소 로컬 `.git/config`

`ConfigurationLevel.Local`로만 쓴다.

- 외부 git 클라이언트·VS Code도 같은 값을 쓰게 되어 도구 안팎이 갈라지지 않는다
- 사용자의 다른 프로젝트를 건드리지 않는다. SSMS 확장이 개발자의 전역 git 설정을 조용히 바꾸는
  것은 되돌리는 길이 도구 안에 없는 부작용이다
- DBVC 클론은 사람당 1~2개라 반복 비용이 작다

전역에 이미 설정이 있는 사람에게는 아무 일도 일어나지 않는다. `BuildSignature`의 탐색이
로컬 → 전역 → 시스템이므로 전역만으로 신원이 성립하고, 배너 자체가 뜨지 않는다.

### 2.3 클론 시점에 묻지 않는다

검토한 대안이다. 넣지 않는 이유는 둘이다.

- `RepositoryConnectDialog`는 이미 입력 항목이 6개다. 신원 두 칸을 더하면 연결 자체가 무거워진다
- **"이미 받아둔 폴더를 연결" 갈래를 덮지 못한다.** 신원이 없는 저장소는 클론으로만 생기지 않는다

배너는 두 갈래를 모두 덮고, 클론 직후에도 즉시 뜬다. 같은 일을 두 곳에서 하지 않는다.

## 3. 설계

### 3.1 `GitIdentity` — 읽고, 쓰고, 검증한다

Core에 새로 둔다. `RepositoryEncoding`과 같은 결의 정적 클래스다.

```
CommitIdentity? Read(string repoPath)          // 하나라도 비면 null
void            Write(string repoPath, name, email)   // ConfigurationLevel.Local
string?         Validate(string? name, string? email) // 통과면 null, 아니면 한국어 사유
```

`Validate`는 순수 함수다. DB도 Git도 닿지 않으므로 판정의 유일한 시험대가 여기다.
검증 규칙은 형식까지만 본다 — 이름이 비지 않을 것, 메일에 `@`와 점이 있고 공백이 없을 것.

**메일이 GitLab 계정과 실제로 일치하는지는 검증하지 않는다.** 폐쇄망 GitLab API를 부르는 것은
이 작업의 범위 밖이고, 부를 수 있다 해도 인증이 또 필요하다.

`IGitManager`에는 넣지 않는다. 인코딩 배너가 `RepositoryEncoding.Detect(mapping.GitPath)`를 정적
호출로 쓰는 선례를 그대로 따른다 — 인터페이스에 얹으면 대역 구현이 함께 늘어나는데 얻는 것이 없다.

### 3.2 폴백 제거와 검사 위치

`DefaultAuthorName`·`DefaultAuthorEmail` 상수와 `??`를 지우고, 신원이 없으면
`GitIdentityMissingException`을 던진다(기존 `Git*Exception` 파일들 옆에 새 파일로 둔다).

**검사는 `BuildSignature` 자리가 아니라 `CommitChanges` 진입부에서 한다.**

```
CommitChanges: ResolveRepoPath → MappingPolicy 검사 → [신원 검사] → Repository 열기
               → Commands.Stage(:317~324) → HasStagedChanges → BuildSignature(:333)
```

`Commands.Stage`가 서명보다 **먼저** 돈다. `BuildSignature` 자리에서 던지면 스테이징만 된 채
실패해 작업 트리 상태가 바뀐다. 차단은 아무것도 바꾸지 않아야 한다.

`PullChanges`는 서명을 만드는 `:356`이 이미 부작용 이전이므로 그 자리에서 던져도 된다.
다만 화면은 Pull을 시작하기 전에 먼저 묻는다(3.5).

### 3.3 배너

`ViewChangesControl.xaml`의 인코딩 배너 아래, 같은 색·같은 어휘로 둔다.

- 문구: "커밋 작성자가 설정되어 있지 않습니다. 지금 커밋하면 누가 바꿨는지 이력에 남지 않습니다."
- 버튼: **작성자 설정...**
- 판정: `ProbeContext`(백그라운드)에서 `GitIdentity.Read`, `ApplyContextProbe`가 옮긴다.
  config 파일을 여는 일이라 UI 스레드에서 부르지 않는다 — 인코딩 판정과 같은 이유다
- 매핑이 없으면 판정하지 않는다. 가리킬 저장소가 없다

**인코딩 배너와 달리 모드 조건을 걸지 않는다.** 배포·감사 클론은 커밋하지 않지만 Pull은 하고,
비-fast-forward Pull은 병합 커밋을 만들어 같은 신원을 요구한다(1.2). 모드로 걸러 버리면 그
사람만 배너 없이 차단당해 빠져나올 길이 없다.

### 3.4 `CommitIdentityDialog`

이름·메일 두 칸, 안내문, 확인/취소. 안내문은 저장 위치를 밝힌다 —
"이 저장소에만 저장됩니다(`.git/config`). 다른 저장소에는 영향이 없습니다."

`IRepositoryConnectDialog` + `RepositoryConnectDialogAdapter` 패턴을 그대로 복제해
`ICommitIdentityDialog`와 어댑터로 두고 `DbvcServices`에서 배선한다. ViewModel 테스트가 WPF 없이
돌아야 한다.

**초깃값은 Windows 계정에서 추정한다.** `Services/WindowsAccountIdentity`가
`secur32.GetUserNameEx`를 `NameDisplay`(표시 이름)와 `NameUserPrincipal`(UPN)로 두 번 부른다.
도메인 가입 PC에서 UPN은 대개 사내 메일과 같다.

- 도메인에 가입되지 않은 계정에서는 두 형식 모두 실패한다. 이름은 `Environment.UserName`으로
  대체하고 메일은 빈 칸으로 둔다
- 어댑터에는 판단을 두지 않는다. 형식 검증은 `GitIdentity.Validate`가 한다.
  SSMS 어댑터에서 판단 로직을 `SsmsUrn`으로 뺀 것과 같은 규칙이다
- **추정값을 자동 확정하지 않는다.** 틀린 메일로 커밋이 나가면 GitLab이 계정에 연결하지 못하고,
  그 사실은 첫 MR에서야 드러난다. 사람이 한 번 보게 한다

### 3.5 차단된 자리에서 이어 가기

`Commit(bool coAuthorConfirmed)`이 이미 쓰는 재진입 패턴을 그대로 쓴다 —
백그라운드에서 판정하고, UI 스레드에서 묻고, 같은 경로를 다시 탄다.

```
Commit(coAuthorConfirmed, identityPrompted)
  백그라운드: [신원 검사] → CoAuthor 검사 → CommitChanges
  UI: NeedsIdentity면 다이얼로그 → Write 성공 시 Commit(coAuthorConfirmed, identityPrompted: true)
```

- 신원 검사를 **CoAuthor보다 앞에** 둔다. 모달 두 개가 연달아 뜨는 것을 피한다
- `identityPrompted`가 참이면 다시 묻지 않는다. 다이얼로그에서 입력받았는데 `Write`가 실패하면
  (파일 권한 등) 또 "신원 없음"으로 돌아와 다이얼로그가 무한히 뜬다. 두 번째는 사유를 알리고 멈춘다
- Pull도 시작 전에 같은 검사를 넣어 다이얼로그 → 재시도로 통일한다. `PullChanges`의 예외는
  화면을 거치지 않는 호출자를 위한 마지막 그물로 남긴다

## 4. 검증

| 확인할 것 | 어디서 |
| --- | --- |
| `Validate`가 빈 이름·`@` 없는 메일·공백 포함을 거른다 | `GitIdentityTests` |
| `Read`/`Write`가 왕복한다 | `GitIdentityTests` |
| **`Write`가 전역이 아니라 로컬에만 쓴다** | `GitIdentityTests` |
| 전역에만 신원이 있어도 `Read`가 값을 낸다 | `GitIdentityTests` |
| `CommitChanges`가 신원 없이 던진다 | `GitManagerTests` |
| **차단 시 인덱스가 그대로다**(스테이징 부작용 없음) | `GitManagerTests` |
| `PullChanges`가 신원 없이 던진다 | `GitManagerTests` |
| 배너 속성이 신원 유무를 따라간다 | `ViewChangesViewModelTests` |
| 다이얼로그가 취소되면 커밋이 일어나지 않는다 | `ViewChangesViewModelTests` |
| 다이얼로그가 확인되면 커밋이 이어진다 | `ViewChangesViewModelTests` |
| **재진입이 한 번뿐이다**(무한 모달 없음) | `ViewChangesViewModelTests` |

인덱스 단언과 재진입 단언은 둘 다 "왜 이 자리인가"를 지키는 테스트다. 없으면 다음 사람이
검사를 `BuildSignature` 자리로 되돌리거나 `identityPrompted`를 지운다.

### 4.1 CI가 검증하지 못하는 것

다이얼로그 렌더링, 배너 표시, `secur32` 추정값의 정확도. `docs/setup-checklist.md`에 절차를 적고
SSMS 21에서 직접 확인한다.

1. 전역 config가 없는 계정에서 클론 → 배너가 뜬다 → 버튼으로 설정 → 배너가 사라진다
2. 배너를 무시하고 커밋 → 막히고 다이얼로그가 뜬다 → 입력하면 커밋이 이어진다
3. 그 커밋의 작성자가 `git log`와 GitLab에서 본인으로 보인다
4. 전역 config가 이미 있는 계정에서는 배너가 뜨지 않는다
5. 설정 후 `.git/config`에만 값이 들어갔고 전역은 그대로다

### 4.2 기존 테스트 파급 — 이 작업의 산출물로 친다

1.3의 71개 호출이 폴백 제거와 함께 드러난다. 예외 메시지를 보고 하나씩 고치지 않는다.
저장소를 만드는 팩토리 세 곳에서 로컬 config를 심는다.

- `NewRepoWithCommit`
- `NewClonedRepoWithBareOrigin`
- 클론 대상을 직접 만드는 `CloneRepository` 테스트들

그러면 테스트가 실행 기계의 전역 설정에 의존하지 않게 된다. 폴백을 없애는 것의 목적이
프로덕션 결함을 막는 것이라면, 이 수정은 그것이 드러낸 테스트 결함을 닫는 것이다.

## 5. 범위 밖

- **이미 만들어진 `DBVC User` 커밋은 고치지 않는다.** 이력 재작성이고 저장소를 가진 모두가 클론을
  다시 받아야 한다(1.1). 팀 배포 *전*이라 대상이 몇 개뿐이므로 지금 막는 것으로 충분하다
- **메일이 GitLab 계정과 일치하는지 확인하지 않는다.** 형식만 본다(3.1)
- **전역 config에 쓰는 선택지를 주지 않는다.** 되돌리는 길이 도구 안에 없다(2.2).
  전역을 쓰고 싶은 사람은 이미 전역에 설정해 두었고, 그 사람에게는 배너가 뜨지 않는다
- **클론 다이얼로그는 건드리지 않는다**(2.3)
- **커밋 작성자와 committer를 구분하지 않는다.** 지금도 같은 서명을 둘 다에 쓰고 있고,
  나눌 이유가 이 작업에는 없다
- 서명 시각·GPG 서명 — 요구가 없다

## 6. 릴리스

`0.5.17`. 스키마 버전은 바뀌지 않는다 — 데이터베이스를 건드리지 않는 변경이다.

`README.md`와 `docs/setup-checklist.md`에 "첫 커밋 전에 작성자를 설정한다"를 적고,
`docs/team-rollout-backlog.md`의 1번을 완료로 옮긴다.

배포 순서에 조건이 없다. 각자 올리고 각자 설정하면 된다 — 인코딩 전환과 달리 저장소를 공유하는
작업이 아니다.
