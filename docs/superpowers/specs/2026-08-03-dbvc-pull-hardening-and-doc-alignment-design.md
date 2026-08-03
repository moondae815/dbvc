# DBVC Pull 견고화 및 문서·코드 정합화 설계

## 1. Overview

Pull을 UI에 노출하면서 드러난 Core 경계 두 가지를 닫고, 설계 문서와 코드가 어긋난 8곳(P3)을 정리한다.

| 갈래 | 내용 |
| --- | --- |
| Core A | `Commands.Pull`이 던지는 `CheckoutConflictException`이 그대로 새어 나간다 |
| Core B | `PullOptions()`에 `CredentialsProvider`가 없어 인증이 필요한 원격에서 항상 실패한다 |
| P3 | 문서·코드 불일치 8건 — 코드 정정 2건, 문서 정정 6건 |

Pull 실패 경로가 libgit2의 영문 원문으로 노출되는 것이 두 Core 항목의 공통 증상이다.
`ViewChangesViewModel.cs:292-300`의 catch-all은 원인을 특정하지 못한 채 "흔한 원인" 힌트를 덧붙이는 임시 조치이며,
그 주석 자체가 이 문서의 작업을 후속 과제로 지목하고 있다.

## 2. Scope

### In Scope

* `WorkingTreeConflictException`과 `GitAuthenticationException`을 `DBVC.Core`에 추가하고 `PullChanges`가 던지게 한다
* `PullOptions`에 Windows 통합 인증(NTLM/Kerberos) 자격 증명 핸들러를 붙인다
* Vsix의 Pull 오류 문구를 예외 타입별로 분기한다
* `ScriptGenerator` 헤더에 제외된 객체를 기록한다 (P3 #6)
* 스크립트 생성 결과를 경고 배너가 아니라 알림 대화상자로 알린다 (P3 #7)
* 설계 문서·README 6곳을 코드에 맞게 정정한다 (P3 #8~#13)

### Out of Scope

* **자격 증명 입력 UI와 저장.** 사용자명/PAT를 묻는 대화상자는 보안 저장소(DPAPI), 만료 처리,
  삭제 UI가 딸린 별도 서브시스템이다. 이번에는 Windows 통합 인증만 처리하고,
  그 외 원격은 SSH 키나 URL에 포함된 자격 증명으로 해결하도록 안내한다.
* **State 아이콘 컬럼** (P3 #9). 문서를 코드에 맞춘다.
* **Active Database 자동 인식** (P3 #8). Object Explorer 연동이 전제이며 Feature 10과 같은 이유로 막혀 있다.
* **`StateTracker` 폴링** (P3 #13). 수동 Refresh를 유지하고 문서에서 폴링 문구를 걷어낸다.
* **`ViewChangesViewModel`의 구조적 분할.** 이 작업이 요구하는 변경이 아니다.

## 3. Component Design

### 3.1. Pull 예외 (Core A)

#### 3.1.1. 새 예외 두 개

`MergeConflictException.cs`와 같은 형태로 파일 하나씩 만든다. 둘 다 `Exception`을 직접 상속한다.

```
WorkingTreeConflictException(string message)
WorkingTreeConflictException(string message, Exception innerException)

GitAuthenticationException(string message)
GitAuthenticationException(string message, Exception innerException)
```

**`MergeConflictException`을 재사용하지 않는 이유.** 두 상황은 사용자에게 할 말이 정반대다.
`MergeConflictException`은 병합이 진행되다 충돌해 hard reset으로 되돌아간 상태 — 추적 중인 파일의
미커밋 변경이 함께 사라졌을 수 있다. `WorkingTreeConflictException`은 병합 체크아웃이 **시작조차
거부된** 상태 — 저장소는 손대지 않은 그대로이고 잃은 것이 없다.

#### 3.1.2. `PullChanges`의 예외 변환

`Commands.Pull` **호출만** `try`로 감싼다. 뒤따르는 `MergeStatus.Conflicts` 판정과
`MergeConflictException` 던지기는 `try` 밖에 둔다 — 안에 넣어도 `MergeConflictException`은
`LibGit2SharpException`이 아니라 잡히지 않지만, 경계를 좁혀 두는 편이 읽기에 분명하다.

`CheckoutConflictException`은 `LibGit2SharpException`의 파생 타입이므로 **먼저 잡아야 한다.**
순서가 뒤집히면 겹치는 미커밋 변경이 인증 오류로 보고된다.

```
catch (CheckoutConflictException ex)
    → WorkingTreeConflictException(
        "'{repoPath}' 저장소에 받아올 변경과 겹치는 미커밋 변경이 있어 Pull하지 않았습니다. " +
        "저장소는 변경되지 않았습니다. 해당 변경을 커밋하거나 되돌린 뒤 다시 시도하세요.", ex)

catch (LibGit2SharpException ex) when (requiresUserCredentials)
    → GitAuthenticationException(
        "'{repoPath}' 저장소의 원격이 사용자 자격 증명을 요구합니다. " +
        "DBVC는 Windows 통합 인증만 지원하므로, SSH 키를 사용하거나 " +
        "원격 URL에 액세스 토큰을 포함해 다시 시도하세요.", ex)
```

`requiresUserCredentials`는 3.1.3의 핸들러가 세우는 지역 변수다.

기존 `MergeStatus.Conflicts` 분기와 `AbortMerge`는 그대로 둔다. `CheckoutConflictException`은
병합 상태가 만들어지기 전에 던져지므로 되돌릴 것이 없고, `AbortMerge`를 부르면 안 된다.

#### 3.1.3. 자격 증명 핸들러 (Core B)

**문자열 매칭을 쓰지 않는다.** libgit2는 인증 실패를 원문 메시지의 `LibGit2SharpException`으로 던지며,
그 문구는 libgit2 버전과 전송 방식(HTTP/SSH)에 따라 달라진다. 대신 **핸들러가 호출되는 시점에**
원격이 무엇을 요구하는지 보고 판정한다.

판정 로직은 순수 정적 메서드로 분리해 단위 테스트 대상으로 만든다.

```
internal static Credentials ResolveCredentials(
    SupportedCredentialTypes types,
    out bool requiresUserCredentials)
```

`CredentialsHandler`가 넘겨주는 `url`과 `usernameFromUrl`은 받지 않는다. 판정에 쓰이지 않고
오류 문구는 이미 `repoPath`로 만들기 때문이다.

`internal`로 두어도 `StateTracker.cs:11`의 `[assembly: InternalsVisibleTo("DBVC.Core.Tests")]` 덕분에
단위 테스트에서 직접 호출할 수 있다.

| `types` | 반환 | `requiresUserCredentials` |
| --- | --- | --- |
| `Default` 포함 | `new DefaultCredentials()` | `false` |
| `Default` 미포함 | `new DefaultCredentials()` | `true` |

두 경우 모두 `DefaultCredentials`를 반환한다. 핸들러는 `Credentials`를 반드시 돌려줘야 하고,
`Default`를 지원하지 않는 원격에서는 어차피 인증이 실패한다. 우리가 여기서 하는 일은
**실패의 원인을 기록해 두는 것**이지 실패를 막는 것이 아니다.

`PullChanges`에서의 연결:

```
var requiresUserCredentials = false;
var options = new PullOptions
{
    FetchOptions = new FetchOptions
    {
        CredentialsProvider = (url, user, types) =>
        {
            var credentials = ResolveCredentials(types, out var needsUser);
            if (needsUser) requiresUserCredentials = true;
            return credentials;
        }
    }
};
```

**기존에 동작하던 원격이 깨지지 않는 이유.** SSH 키를 쓰는 원격은 `CredentialsProvider`가 아니라
libgit2의 SSH 에이전트 경로를 타므로 핸들러가 호출되지 않거나 호출되더라도 키 인증이 먼저 성립한다.
URL에 자격 증명이 박힌 원격도 마찬가지로 핸들러 이전에 인증이 끝난다.
인증이 필요 없는 원격(로컬 경로, 익명 HTTP)에서는 핸들러가 호출되지 않는다.
즉 핸들러 추가는 지금까지 **실패하던 경로에만** 영향을 준다.

### 3.2. Vsix의 Pull 오류 문구

`ViewChangesViewModel.Pull`의 catch-all에서 힌트를 덧붙이는 코드를 제거하고 타입별로 나눈다.

| 예외 | 제목 | 내용 |
| --- | --- | --- |
| `MergeConflictException` | `DBVC Pull 중단` | `ex.Message` (기존과 동일) |
| `WorkingTreeConflictException` | `DBVC Pull 중단` | `ex.Message` |
| `GitAuthenticationException` | `DBVC Pull 실패` | `ex.Message` |
| 그 외 | `DBVC Pull 실패` | `ex.Message` (힌트 없음) |

`WorkingTreeConflictException`이 `Pull 중단`인 이유는 사용자 관점에서 아무 일도 일어나지 않았기 때문이다.
`실패`는 예상 못 한 오류에 남겨 둔다.

**힌트를 제거하는 근거.** 지금의 catch-all은 원격 미설정 같은 무관한 오류에도
"겹치는 미커밋 변경이 있으면…"을 덧붙인다. 원인이 타입으로 갈리면 그 추측이 필요 없어진다.

#### 3.2.1. 확인 대화상자 문구 정정

현재 문구는 두 결과를 한 문장에 뭉쳐 놓았다.

> 받아올 변경과 겹치면 Pull이 거부되거나, 병합이 진행되다 충돌해 되돌아가면서
> 추적 중인 파일의 변경이 함께 사라질 수 있습니다.

거부 경로가 무손실임이 확정됐으므로 두 결과를 분리한다.

> 커밋하지 않은 변경 N개가 있습니다.
> 받아올 변경과 겹치면 Pull이 거부됩니다. 이 경우 저장소는 그대로입니다.
> 겹치지 않더라도 병합 중 충돌이 나면 병합을 되돌리면서 추적 중인 파일의 변경이 함께 사라질 수 있습니다.
> (DBVC가 추출한 내용은 Refresh로 다시 만들 수 있습니다)
>
> 계속하시겠습니까?

개수 `N`은 `GetChangedFiles`의 결과이며 미추적 파일을 포함한다. 문구가 그 개수를 손실량으로
단정하지 않으므로 P2 리뷰가 지적한 과대 계상 문제는 유지되지 않는다.

### 3.3. 스크립트 헤더의 제외 기록 (P3 #6)

**스펙의 전제가 틀렸다.** script-generation 설계 3.1은 "내용이 비어 있는 섹션은 건너뛰되,
건너뛴 사실을 헤더에 기록한다"고 적었으나, `ScriptExporter`가 빈 SQL을 미리 걸러
`BuildScript`에 넘긴다(`ScriptExporter.cs:50-55`). `ScriptGenerator`의 자체 빈 섹션 필터는
프로덕션 경로에서 발동하지 않으므로, 그 개수를 헤더에 적으면 항상 0이다.

기록할 가치가 있는 것은 `ScriptExporter`가 아는 **제외된 객체**다.

```
public static string BuildScript(
    IEnumerable<ScriptSection>? sections,
    ScriptKind kind,
    DateTimeOffset generatedAt,
    IReadOnlyCollection<string>? excludedObjects = null)
```

헤더 출력:

```sql
/* ============================================================
   DBVC Rollback Script
   Generated: 2026-08-03T10:00:00+09:00
   Objects: 3
   Excluded: 2 (dbo.OldTable, dbo.usp_Legacy)
   ============================================================ */
```

`excludedObjects`가 `null`이거나 비면 `Excluded` 줄을 넣지 않는다. 기존 출력이 그대로 유지되므로
현재 헤더를 검사하는 테스트가 깨지지 않는다.

`ScriptExporter`는 `result.ExcludedObjects`를 넘긴다. 순서는 `Export`가 대상을 순회한 순서를 따른다.

`ScriptGenerator`의 빈 섹션 방어 필터는 그대로 둔다. `BuildScript`는 `public`이고 단위 테스트가
직접 호출하므로 여전히 계약의 일부다.

**대화상자가 아니라 헤더에도 적는 이유.** 알림은 닫으면 사라지지만 헤더는 파일과 함께 남는다.
생성한 스크립트를 나중에 다른 사람이 열었을 때 무엇이 빠졌는지 알 수 있어야 한다.

### 3.4. 스크립트 생성 결과 알림 (P3 #7)

`GenerateScript`가 `WarningMessage`를 쓰는 것을 중단한다.

`WarningMessage`는 지속 상태(매핑 안 됨, SMO 추출 실패)를 표시하는 배너이고 `Refresh`가 덮어쓴다.
스크립트 생성은 사용자의 일회성 동작이므로 대화상자가 맞다.

| 상황 | 호출 |
| --- | --- |
| 저장 성공 | `ShowInfo(title, "3개 객체를 내보냈습니다." + 제외 문구)` |
| 내보낼 내용 없음 | `ShowInfo(title, "내보낼 내용이 없습니다." + 제외 문구)` |
| 파일 쓰기 실패 | `ShowError(...)` (기존과 동일) |
| 사용자가 저장 대화상자 취소 | 아무 알림도 하지 않음 (기존과 동일) |

`title`은 기존 저장 대화상자와 같은 `DBVC Deployment Script` / `DBVC Rollback Script`다.

**제외 문구는 `ScriptKind`에 따라 달라진다.** 제외 사유가 다르기 때문이다 —
Rollback은 `GetFileContentBeforeLastCommit`가 `null`을 준 것(되돌릴 이전 리비전 없음)이고,
Deployment는 작업 트리에 `.sql` 파일이 없는 것이다(`ScriptExporter.cs:39-47`).

* Rollback → `"2개 객체는 이전 리비전이 없어 제외했습니다: dbo.A, dbo.B"`
* Deployment → `"2개 객체는 추출된 파일이 없어 제외했습니다: dbo.A, dbo.B"`

제외가 없으면 이 줄을 붙이지 않는다.

"내보낼 내용이 없음"에 `ShowError`를 쓰지 않는 이유는 오류가 아니기 때문이다.
`ShowError`는 실제 실패(파일 쓰기, Git 작업)에 남겨 둔다.

### 3.5. 문서 정정 (P3 #8~#13)

| # | 파일·위치 | 정정 |
| --- | --- | --- |
| 8 | `2026-07-31-dbvc-view-changes-design.md:48` | "the active database"가 자동 인식된다는 함의를 제거하고 Server/Database 수동 입력 + Connect임을 명시한다. 자동 인식은 Object Explorer 연동이 전제이므로 Feature 10과 함께 보류 중임을 적는다 |
| 9 | `2026-07-31-dbvc-view-changes-design.md:27` | "an Icon (indicating M/A/D state)" → State를 텍스트로 표시하는 컬럼. 아이콘은 별도 과제 |
| 10 | `2026-07-31-dbvc-ssms21-plugin-design.md:30` | `UiController` 항목을 실제 구조로 교체 — `ViewChangesToolWindow`(창 등록), `ViewChangesControl`(WPF `UserControl`), `ViewChangesViewModel`과 `RelayCommand`(MVVM) |
| 11 | `2026-08-01-dbvc-script-generation-design.md:33` | `BuildScript` 시그니처를 3.3의 최종형(`generatedAt`, `excludedObjects` 포함)으로 갱신. `generatedAt`을 인자로 받는 이유(결정적 출력·테스트 가능성)를 한 줄 덧붙인다 |
| 12 | `2026-08-01-dbvc-script-generation-design.md:66-70` | "Rollback의 이전 리비전 정의"에 삭제된 객체 규칙을 추가한다 — HEAD에 없는 경로는 `QueryBy`가 빈 결과를 주므로, 커밋을 최신순으로 거슬러 파일이 마지막으로 존재했던 시점의 내용을 쓴다 (`GitManager.cs:287-292`) |
| 13 | `2026-07-31-dbvc-ssms21-plugin-design.md:35` | "주기적으로(또는 수동 새로고침 시)" → 수동 새로고침 시에만. 폴링은 구현되어 있지 않고 계획도 없음을 명시 |
| 13 | `README.md:6` | "실시간으로 감지하고 추적합니다" → DDL 트리거가 실시간으로 `DBVC_ChangeLog`에 기록하고, 화면 반영은 Refresh 시점임을 구분해 적는다 |

추가로 script-generation 설계 3.1의 "내용이 비어 있는 섹션은 건너뛰되, 건너뛴 사실을 헤더에 기록한다"를
3.3의 실제 구조에 맞게 다시 쓴다 — 제외 판정은 `ScriptExporter`가 하고 `BuildScript`는 전달받아 기록한다.

3.4의 변경에 맞춰 script-generation 설계 4절의 "생성 결과를 요약해 알린다"에
알림 수단이 `IUserNotifier.ShowInfo`임을 명시한다.

## 4. Error Handling

* **겹치는 미커밋 변경:** `WorkingTreeConflictException`. 저장소는 변경되지 않는다. `AbortMerge`를 호출하지 않는다.
* **병합 충돌:** 기존과 동일. `AbortMerge` 후 `MergeConflictException`.
* **사용자 자격 증명 요구:** `GitAuthenticationException`. 원격 상태는 변하지 않는다.
* **원격 미설정:** 기존과 동일한 `InvalidOperationException`.
* **스크립트 생성 실패:** 파일 쓰기만 `ShowError`. 제외·빈 결과는 `ShowInfo`.

## 5. Testing Strategy

**Core 단위 테스트**

* `ResolveCredentials` — `Default` 포함 시 `requiresUserCredentials == false`,
  `UsernamePassword`만 있을 때 `true`. 두 경우 모두 `DefaultCredentials`를 반환한다
* `PullChanges`가 `CheckoutConflictException`을 `WorkingTreeConflictException`으로 감싸는지.
  `GitManagerTests`에 이미 실제 로컬 원격을 쓰는 Pull 테스트가 있다(`GitManagerTests.cs:445-505`).
  같은 패턴으로 원격에서 파일을 커밋하고 로컬에서 같은 파일을 커밋하지 않은 채 수정한 뒤 Pull한다.
  `InnerException`이 보존되는지, 저장소 HEAD가 그대로인지도 확인한다
* `BuildScript`의 `Excluded` 줄 — 제외가 있을 때 개수와 이름이 나오는지,
  `null`·빈 목록일 때 줄 자체가 없는지
* `ScriptExporter`가 `ExcludedObjects`를 `BuildScript`에 전달하는지 (생성된 스크립트 헤더로 확인)

**Vsix 단위 테스트**

* Pull이 각 예외 타입에 대응하는 제목·문구를 내는지. `WorkingTreeConflictException`의 문구에
  "저장소는 변경되지 않았습니다"가 들어가는지
* catch-all이 더 이상 미커밋 힌트를 덧붙이지 않는지 — 관계없는 예외를 던지게 하고 문구에
  힌트 문자열이 없음을 확인한다
* `GenerateScript`가 성공 시 `ShowInfo`를 부르고 `WarningMessage`를 건드리지 않는지
* 제외가 있을 때와 없을 때의 알림 문구
* 저장 대화상자 취소 시 아무 알림도 하지 않는지

**수동 검증 대상 (SSMS 21)**

인증이 필요한 실제 원격에 대한 Pull은 CI에서 재현할 수 없다. 사내 Azure DevOps 등
Windows 통합 인증 원격에서 Pull이 성공하는지, 자격 증명을 요구하는 원격에서
안내 문구가 뜨는지는 수동으로 확인한다.

## 6. 기존 테스트에 미치는 영향

* `BuildScript`의 새 인자는 기본값이 있어 기존 호출부와 테스트가 그대로 컴파일된다
* `GenerateScript`가 `WarningMessage` 대신 `ShowInfo`를 쓰므로
  `ViewChangesViewModelTests.cs:978`의 `GenerateDeploymentScriptCommand_ReportsExcludedObjectsAfterSaving`과
  `:965`의 `GenerateRollbackScriptCommand_WarnsAndSkipsSave_WhenNoObjectHasAPreviousRevision`이 깨진다.
  두 테스트를 알림 검사로 고친다
* Pull의 catch-all 문구 검사가 있으면 타입별 분기에 맞춰 고친다
