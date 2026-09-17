# 배포·감사 클론에서 병합한다 — 설계

> **성격: 기능 추가이자 결정 번복.** 병합을 도구 밖(GitLab MR)에 두었던 결정
> ([브랜치 조작 설계](2026-09-10-dbvc-branch-operations-design.md) 2.4, 백로그 6번)을 좁혀
> 뒤집는다. 충돌 해결은 여전히 밖이다. 백로그 11번(경고 A)을 이 설계로 닫는다.

## 1. 문제

티켓 한 바퀴 중 병합만 도구 밖에 남아 있다.

| | 하는 일 | 지금 어디서 |
| --- | --- | --- |
| 1 | 티켓 브랜치 생성 → 변경 → 커밋 → Push | DBVC |
| 2 | `develop`에 병합 | **GitLab** |
| 3 | 테스트 클론에서 Pull → 차이 검사 → 스크립트 실행 → 재검사 | DBVC |
| 4 | `master`에 병합 | **GitLab** |
| 5 | 운영 클론에서 같은 루프 | DBVC |

팀은 "다른 외부 도구 없이 DBVC에서"를 원한다. 그리고 병합과 배포 사이가 끊겨 있는 것 자체가
위험이다 — **DB는 병합해도 1바이트도 바뀌지 않는다**([워크플로 설계](2026-08-24-dbvc-git-workflow-design.md)
2.1). 병합한 사람과 배포하는 사람이 다르면 "병합했으니 나갔겠지"가 생긴다.

### 1.1 전제 — 직접 Push 권한

2026-09-17 확인: **개발자는 `develop`에, DBA는 `master`에 직접 Push할 권한이 있다.** 이 설계
전체가 여기에 걸려 있다. 조직이 보호 브랜치를 "MR로만 병합"으로 바꾸면 `MergeAndPush`의 Push가
거부되고(`PushRejected`와 구분되지 않는 원격 거부로 나타난다) 이 기능은 쓸 수 없다.

### 1.2 GitLab MR API 연동을 택하지 않은 이유

사용자가 처음 그린 흐름("병합 요청 → 목록 확인 → 병합")과 정확히 같은 것은 GitLab API다.
택하지 않는다.

- API는 **HTTPS + 개인 토큰**이다. SSH 전용 설계가 없앤 경로(폐쇄망 사설 CA, 자격증명 보관)를
  되살린다.
- 토큰을 디스크에 둬야 한다 — AI API 키에 이어 두 번째 예외가 된다.
- GitLab 버전에 API가 묶이고 CI가 검증하지 못한다.

잃는 것은 **리뷰·승인 기록**이다. 조직이 그것을 요구하게 되면 이 설계가 아니라 MR 연동을 다시 본다.

## 2. 결정

### 2.1 병합은 배포·감사 클론에서만, 목적지는 고정 브랜치 하나

`deploy`(`develop` 고정)와 `audit`(`master` 고정) 클론에서 병합한다. **목적지는 매핑의 고정
브랜치(`MappingConfig.Branch`)** 이므로 고르는 화면이 없다.

개발 클론(`write`)에서는 하지 않는다. 브랜치가 자유라 목적지가 흔들리고, 병합 직후 이어야 할
차이 검사가 그 모드에서 금지다(`Compare`는 `write`에서 X).

`audit`가 "읽기 전용"이라는 성질은 **DB에 대해서** 유지된다. 병합은 저장소만 바꾼다.

### 2.2 "병합 요청"은 미병합 원격 브랜치 목록이다

MR이 없으므로 요청의 표지를 따로 만들지 않는다. 원격 브랜치 중 **끝 커밋이 목적지에 아직 들어
있지 않은 것** 전부가 후보다. 병합할 준비가 되었는지는 지라 티켓 상태로 사람이 판단한다.

명시적 요청 ref(`refs/dbvc/requests/...`)는 검토하고 배제했다 — GitLab 화면에 보이지 않고,
병합 뒤 치우는 경로를 따로 만들어야 한다.

### 2.3 `develop`과 `master`는 병합 원본이 될 수 없다

목록에서 빼고 `MergeAndPush` 입구에서 한 번 더 거부한다. `master` 목록에 `develop`이 뜨면
**`develop`을 통째로 운영에 병합하는 것이 버튼 한 번**이 된다 — 환경 브랜치 패턴에서 가장 나쁜
사고다. 반대 방향(`develop` 목록의 `master`)은 hotfix가 `develop`에 병합되지 않은 동안 늘 뜨는
잡음이다.

이름을 코드에 박는다. [브랜치 조작 설계](2026-09-10-dbvc-branch-operations-design.md) 3.5는 티켓
이름 규칙을 코드에 넣지 않았는데, 그것은 조직이 바꿀 수 있는 규칙이기 때문이었다. `develop`/`master`는
[워크플로 설계](2026-08-24-dbvc-git-workflow-design.md) 1.1이 전제로 삼은 환경 브랜치 이름이라
성격이 다르다. 비교는 대소문자를 구분한다(git 브랜치 이름과 같다).

### 2.4 `Push` 금지는 그대로 두고, 병합 커밋 하나만 올린다

`MappingPolicy`가 배포·감사 클론의 `Push`를 막는 사유는 "그 전에 만들어진 로컬 커밋이 섞여
나간다"이다. 그 사유는 병합에도 똑같이 적용되므로 `Push`를 열지 않는다. 대신 `MergeAndPush`가
**로컬이 원격보다 앞서 있으면 시작하지 않고**, 자기가 만든 병합 커밋 하나만 올린다.

### 2.5 올라가지 않은 병합 커밋을 로컬에 남기지 않는다

Push가 어떤 이유로든 실패하면 로컬을 병합 전으로 되돌린다. 남기면 다음 병합이 2.4의 검사에
걸리고, 그 클론은 `Push`가 금지라 **도구 안에서 빠져나올 길이 없다** — 백로그 8번과 같은 막다른 길을
도구가 스스로 만드는 셈이다.

### 2.6 병합 커밋을 항상 만든다

fast-forward가 가능해도 `--no-ff`로 병합한다. fast-forward하면 "언제 무엇을 운영에 병합했나"가
이력에서 사라지고, 되돌릴 단위도 없어진다. 이력 탭은 이미 병합 커밋을 `병합`으로 표시한다.

### 2.7 충돌은 풀지 않는다

미리보기가 충돌을 먼저 계산해 병합 버튼을 잠근다. 푸는 것은 브랜치 작성자가 GitLab이나 Git
클라이언트에서 한다. 이 경계는 "외부 도구 없이"를 100% 채우지 못하지만, 도구 안의 충돌 해결 UI는
크고 틀리면 되돌리기 어렵다.

### 2.8 경고 A는 운영 병합에서 줄 단위로 판정한다

3.4. 원래 설계(3.10)의 `P@develop != P@master`는 폐기한다 — 테스트를 거친 브랜치는 `develop`에
자기 변경이 들어 있으므로 운영 병합마다 모든 객체에서 뜬다. 커밋 시점의 경고 A(개발 클론)도 만들지
않는다. 커밋할 때는 목적지를 모른다.

## 3. 설계

### 3.1 `MappingPolicy`

`DbvcOperation.Merge`를 더한다.

```csharp
case DbvcOperation.Merge:
    // 목적지가 고정 브랜치여야 의미가 있다 - 개발 클론은 브랜치가 자유라 목적지가 흔들리고,
    // 병합 직후 이어야 할 Compare가 write에서 금지다. DB에는 쓰지 않으므로 audit도 허용한다.
    return mode != MappingMode.Write;
```

`GetDisplayName`에 `"병합"`. `Push`의 판정은 바꾸지 않는다(2.4).

### 3.2 `IGitManager` — 더하는 표면

```csharp
/// Fetch한 뒤, 고정 브랜치에 아직 병합되지 않은 원격 브랜치를 마지막 커밋 시각 내림차순으로 낸다.
/// develop/master는 빠진다. 통신 실패는 Pull과 같은 예외로 전파된다.
IReadOnlyList<UnmergedBranch> GetUnmergedBranches(string serverName, string databaseName);

/// 작업 트리를 건드리지 않고 병합 결과를 계산한다. 네트워크를 쓰지 않는다 - 마지막 Fetch 기준이다.
MergePreview PreviewMerge(string serverName, string databaseName, string sourceBranch);

/// 병합 커밋을 만들고 그 커밋만 Push한다. 실패하면 로컬을 시작 전 상태로 되돌린다.
MergeOutcome MergeAndPush(string serverName, string databaseName, string sourceBranch);
```

`sourceBranch`는 remote 접두사를 뗀 이름이다(`GetBranches`의 `BranchInfo.Name`과 같은 규칙).
원격은 현재 브랜치의 `RemoteName`을 쓴다.

```csharp
public sealed class UnmergedBranch
{
    public string Name;
    public string LastCommitAuthor;
    public DateTimeOffset LastCommitTime;
    /// 목적지에 없는 커밋 수.
    public int CommitCount;
    /// 끝 커밋이 원격 develop에 들어 있는가. 목적지가 master일 때만 값이 있다 - "테스트 반영" 열.
    public bool? IsInDevelop;
}

public sealed class MergePreview
{
    /// 목적지 끝 → 병합 결과 사이에 바뀌는 파일(저장소 상대 경로, '/' 구분).
    public IReadOnlyList<string> ChangedPaths;
    /// 비어 있지 않으면 병합할 수 없다.
    public IReadOnlyList<string> ConflictPaths;
    /// 목적지가 master일 때만 채워진다(3.4).
    public IReadOnlyList<PromotionLeak> Leaks;
    public bool AlreadyMerged;
}

public enum MergeOutcomeKind { Merged, AlreadyMerged, Conflicts, PushRejected, LocalAhead, Refused }

public sealed class MergeOutcome
{
    public MergeOutcomeKind Kind;
    /// 한국어. 화면이 그대로 띄운다. Merged이면 null.
    public string? Message;
    /// Conflicts면 충돌 파일, Merged면 바뀐 파일.
    public IReadOnlyList<string> Paths;
}
```

예상할 수 있는 결과는 값으로, 통신·인증 실패는 기존 예외(`GitRemoteException`,
`GitAuthenticationException`)로 돌려준다 — `PushChanges`의 `NoUpstream` 판단과 같은 기준이다.

**미리보기는 작업 트리 없이 계산한다.** `repo.ObjectDatabase.MergeCommits(ours, theirs, options)`가
메모리에서 병합 트리(`MergeTreeResult.Tree`)와 충돌(`MergeTreeResult.Conflicts`)을 낸다.
LibGit2Sharp 0.32.0의 net472 XML 문서에서 실재를 확인했다. `ChangedPaths`는 목적지 끝 트리와 그
병합 트리의 `TreeChanges`다.

### 3.3 `MergeAndPush` 순서

1. **입구 검사.** `EnsureAllowed(Merge)`는 기존 메서드와 같이 `OperationNotAllowedException`을
   던진다 — 화면의 `CanExecute`가 이미 막으므로 여기 도달하면 코딩 실수다. 나머지는 하나라도 걸리면
   `Refused`와 사유.
   - 매핑에 고정 브랜치가 있고 HEAD가 그것이다(화면은 `RepositoryStateEvaluator`가 이미 막지만 Core도 본다)
   - 작업 트리가 깨끗하다 — `SwitchBranch`와 같이 `RetrieveStatus`로 직접 센다
   - 원본이 `develop`/`master`가 아니다(2.3)
2. **Fetch.**
3. **로컬이 원격보다 앞서 있으면 `LocalAhead`.** 뒤처져만 있으면 원격 끝으로 fast-forward한다 —
   1번에서 트리가 깨끗했으므로 잃을 것이 없다.
4. 원본 끝이 HEAD의 조상이면 `AlreadyMerged`.
5. `headBefore = repo.Head.Tip`을 기억하고 `repo.Merge(원격 원본 끝, signature,
   FastForwardStrategy.NoFastForward)`. 서명은 `BuildSignature`(신원이 없으면
   `GitIdentityMissingException`, 폴백 없음). 메시지는 `PROJ-123 브랜치를 develop에 병합`.
6. 충돌이면 `AbortMerge(repo, headBefore)` 후 `Conflicts`. 미리보기와 실제 사이에 누군가 Push한
   경합에서만 도달한다.
7. **Push.** 실패하면 **예외 종류와 무관하게** `repo.Reset(ResetMode.Hard, headBefore)`를 먼저 하고,
   ref 거부는 `PushRejected`로 돌려주며 그 밖의 예외는 다시 던진다(2.5).

**hard reset이 안전한 근거는 1번이다.** 트리가 깨끗했다는 사실이 되돌리기의 전제이므로, 1번의
검사를 옮기거나 느슨하게 하면 7번이 사용자 파일을 지운다. 그 자리에 주석으로 남긴다.

**`ExtractionBaseline`과 무관하다.** 배포·감사 클론은 추출하지 않는다.

### 3.4 경고 A — `PromotionLeakDetector`

**목적지가 `master`일 때만** 계산한다. `develop` 병합에서 딸려 온 변경은 원래 있던 곳으로 돌아갈
뿐이다([브랜치 정책 정정](2026-09-09-dbvc-branch-policy-correction-design.md) 3절의 2026-09-17
바로잡음).

`ChangedPaths` 중 `ObjectPathConvention.TryParseRelativePath`를 통과하는 경로 `P`마다:

- **운영으로 가는 줄** `S(P)` = 목적지 끝 → 병합 트리에서 `P`에 **추가된** 줄
- **다른 브랜치의 줄** `B(P)` = 원격 브랜치 `B`(원본·`develop`·`master` 제외, **`master`에 아직 병합되지
  않은 것**)마다 `merge-base(master, B)` → `B`에서 `P`에 추가된 줄

`S(P) ∩ B(P)`가 비어 있지 않으면 `PromotionLeak { Path, BranchName, Lines }`.

```
dbo.usp_Order — PROJ-120 브랜치에도 같은 줄이 4개 있습니다. (예: DiscountRate DECIMAL(5,2))
그 브랜치의 변경이 딸려 왔는지 확인하세요. PROJ-120은 아직 운영에 병합되지 않았습니다.
```

**줄 정규화.** 앞뒤 공백을 자르고 내부 연속 공백을 하나로 줄인다. 대소문자는 구분한다. 정규화 뒤
비었거나 **뻔한 줄**(`GO`, `BEGIN`, `END`, `AS`, `(`, `)`, `,`, `;` 및 이들의 대소문자 변형)은
비교에서 뺀다 — 빼지 않으면 모든 프로시저가 모든 브랜치와 겹친다.

**분리.** `PromotionLeakDetector.Detect(IReadOnlyDictionary<string, IReadOnlyCollection<string>> source,
IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> others)`는 순수
함수다(경로 → 줄 집합, 브랜치 → 경로 → 줄 집합). Git에서 블롭을 읽어 추가된 줄을 뽑는 것은
`GitManager`의 얇은 어댑터가 한다. 기존 원칙("두 브랜치의 파일 내용을 주입해 순수 함수로",
워크플로 설계 4.2)을 따른다.

**비용.** 후보 브랜치 수 × 원본이 바꾼 경로 수만큼 로컬 블롭을 읽는다. 원본이 바꾸지 않은 경로는
읽지 않는다. 네트워크는 쓰지 않는다.

**경고이고 차단이 아니다.** 경고가 있으면 병합 확인 대화상자에 목록이 함께 실린다(경고 B와 같은
방식).

#### 감추지 않을 한계

1. **방향을 모른다.** 겹침은 "B에서 이쪽으로 왔다"일 수도 "이쪽 변경이 B에 딸려 갔다"일 수도 있다.
   문구가 중립이고 판단은 DBA가 한다.
2. **브랜치에 없는 변경은 못 잡는다.** 공용 개발 DB에서 고치고 아직 커밋하지 않은 남의 작업은
   판정할 재료가 없다.
3. **지운 브랜치는 못 잡는다.** 그래서 운영 규칙이 하나 생긴다 — **`master`에 병합하기 전까지 티켓
   브랜치를 지우지 않는다**(6절).
4. **같은 줄을 둘이 따로 썼을 수 있다.** 오탐이다.
5. **마지막 Fetch 기준이다.** 목록을 연 뒤 올라온 브랜치는 빠진다. 미리보기는 목록을 열 때의 Fetch를 쓴다.

### 3.5 화면

배포 패널(`DeploymentViewModel`, 배포·감사 공용)의 차이 검사 위에 **병합 영역**을 붙인다. 새 창을
만들지 않는다.

1. **`병합할 브랜치 확인`** — `IBackgroundScheduler`로 `GetUnmergedBranches`. 누를 때만 갱신한다
   (원격 확인과 같은 규칙). 대상이 바뀌면 목록을 지운다 — 낡은 목록을 최신인 척 두지 않는다.
2. 행을 고르면 `PreviewMerge`. 바뀔 객체(객체 유형 열 포함), 충돌 파일, 경고 A. 충돌이 있으면
   `병합` 버튼을 잠근다. 목적지가 `master`이면 목록에 **테스트 반영** 열(`IsInDevelop`)을 띄운다.
   막지 않는다 — hotfix는 테스트를 건너뛰는 것이 정상이다.
3. **`병합`** → 확인 대화상자 → `MergeAndPush`(백그라운드).

   > `PROJ-123` 브랜치를 `master`에 병합하고 원격에 올립니다.
   > (경고 A가 있으면 그 목록)

4. `Merged`면 **차이 검사를 이어서 시작할지** 묻는다.
   기존 확인 대화상자(`IUserNotifier.Confirm`)로 묻는다. 기본 선택이 취소인 것은 그대로 둔다 —
   잘못 눌러도 검사를 시작하지 않을 뿐 잃는 것이 없다.
   자동으로 돌리지 않는다 — 운영 DB 전체 추출은 오래 걸려 시작 시점은 사람이 정한다.

`CanExecute`는 `MappingPolicy.IsAllowed(mode, DbvcOperation.Merge)`와 바쁨 상태로 판정한다. 작업
트리가 더러운 클론은 `WorkingTreeDirty` 차단 화면이라 병합 영역에 닿지 않는다.

| 결과 | 문구 |
| --- | --- |
| `Merged` | `PROJ-123을 develop에 병합하고 올렸습니다. 차이 검사로 DB에 반영할 것을 확인하세요.` |
| `AlreadyMerged` | `이미 병합되어 있습니다.` — 목록을 다시 읽는다 |
| `Conflicts` | 충돌 파일 + `도구 안에서는 충돌을 풀 수 없습니다. 브랜치 작성자가 GitLab이나 Git 클라이언트에서 풀어야 합니다. develop을 티켓 브랜치로 병합해서 풀면 안 됩니다 — 운영 병합 때 develop 전체가 딸려 갑니다.` |
| `PushRejected` | `그 사이 다른 사람이 원격에 올렸거나 원격이 거부했습니다. 로컬은 병합 전으로 되돌렸습니다. 다시 시도하세요.` |
| `LocalAhead` | `이 클론에 원격에 없는 커밋이 있어 병합하지 않았습니다. 배포·감사 클론은 DBVC가 커밋하지 않으므로 원인은 도구 밖에 있습니다.` |
| `Refused` | Core가 준 사유 그대로 |
| 통신·인증 예외 | 기존 `RemoteDiagnostics` 문구 그대로 |

## 4. 검증

### 4.1 단위 테스트

- `MappingPolicyTests` — `Merge` × 모드 3개.
- `PromotionLeakDetectorTests`
  - `Detect_ReportsLeak_WhenAddedLinesOverlapAnotherBranch`
  - `Detect_IgnoresTrivialLines_WhenOnlyKeywordsOverlap`
  - `Detect_ReturnsEmpty_WhenNoOverlap`
  - `Detect_NormalizesWhitespace_WhenComparingLines`
- `GitManagerTests` — 임시 저장소 + 로컬 bare 원격(기존 방식).
  - `GetUnmergedBranches_ExcludesMergedAndEnvironmentBranches`
  - `GetUnmergedBranches_MarksIsInDevelop_WhenTargetIsMaster`
  - `PreviewMerge_ReportsConflicts_WithoutTouchingWorkingTree`
  - `PreviewMerge_ReportsLeaks_OnlyWhenTargetIsMaster`
  - `MergeAndPush_CreatesMergeCommit_WhenFastForwardIsPossible`
  - `MergeAndPush_RestoresHead_WhenPushIsRejected`
  - `MergeAndPush_ReturnsLocalAhead_WhenLocalHasUnpushedCommits`
  - `MergeAndPush_Refuses_WhenTreeIsDirty`
  - `MergeAndPush_Refuses_WhenSourceIsEnvironmentBranch`
  - `MergeAndPush_Throws_InWriteMode`
- ViewModel — 결과별 문구, 경고가 있을 때 확인 대화상자 경유, `Merged` 뒤 차이 검사 제안,
  `CanExecute`가 `write`에서 false.

### 4.2 SSMS 21에서 직접 누를 것

CI가 검증하지 못한다. 눌러 보기 전에는 "동작한다"고 말하지 않는다.

1. 테스트 클론에서 병합 → GitLab에 병합 커밋이 보이고 이력 탭 **종류**가 `병합`인지
2. `Merged` 뒤 차이 검사 시작 → Pull이 즉시 끝나고 병합된 객체가 차이로 뜨는지
3. 운영 클론에서 같은 객체를 만진 브랜치 둘 → 경고 A가 뜨고 확인 대화상자에 실리는지
4. 충돌하는 브랜치 → 미리보기에서 버튼이 잠기는지
5. 개발 클론에서 병합 영역이 보이지 않는지

## 5. 범위 밖

- **충돌 해결** — 2.7.
- **병합 되돌리기(revert)** — 되돌린 뒤 DB를 어떻게 되돌리는지가 함께 풀려야 한다.
- **브랜치 삭제** — 6절의 규칙과 부딪친다.
- **`develop → master` 통째 병합** — 2.3이 막는다.
- **GitLab MR 연동·리뷰 기록** — 1.2.
- **커밋 시점의 경고 A** — 2.8.

## 6. 운영 규칙과 문서

**새 운영 규칙** — `setup-checklist.md`·`rollout-announcement.md`에 싣는다.

- **`master`에 병합하기 전까지 티켓 브랜치를 지우지 않는다.** 경고 A의 재료다(3.4 한계 3).
- **충돌을 `develop`을 티켓 브랜치로 병합해서 풀지 않는다.** 운영 병합 때 `develop` 전체가 딸려 간다.
- **보호 브랜치를 "MR로만 병합"으로 바꾸면 이 기능이 멈춘다**(1.1).

**함께 고칠 문서.**

| 문서 | 변경 |
| --- | --- |
| `README.md` | 배포·감사 클론의 병합 영역 |
| `docs/user-guide.html` | 테스트·운영 배포 절차에 병합을 넣는다 |
| `docs/setup-checklist.md` | 4.2 수동 검증 항목, 새 운영 규칙 |
| `docs/rollout-announcement.md` | 병합 절차와 규칙, 릴리스 노트 템플릿 |
| `docs/team-rollout-backlog.md` | 6번은 "충돌 해결만 도구 밖"으로, 11번은 이 설계로 닫음 |
| [브랜치 조작 설계](2026-09-10-dbvc-branch-operations-design.md) 2.4 | 번복 표시와 이 문서 링크 |
| [워크플로 설계](2026-08-24-dbvc-git-workflow-design.md) 3.10 | 경고 A 판정 방식 정정 표시 |
| `source.extension.vsixmanifest` | 0.7.2 → **0.8.0** |
