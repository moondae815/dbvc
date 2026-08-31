# DBVC 형상 관리 2차 구현 계획 — Clone과 Fetch

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 저장소를 받는 일과 원격이 앞서 있는지 보는 일을 DBVC 안에서 끝내, 도입할 때 PowerShell에서 `git clone`을 치지 않아도 되게 한다.

**Architecture:** `IGitManager`에 `CloneRepository`와 `FetchRemoteStatus` 둘만 더한다. clone은 매핑이 생기기 전에 일어나므로 다른 API와 달리 `(serverName, databaseName)`이 아니라 원격 주소와 받을 경로를 받는다. **받을 경로는 없는 폴더만 받는다** — 존재하는 폴더는 전부 DBVC가 만든 것이 되므로 실패·취소 뒤처리에서 "지워도 되는 폴더"를 판별할 필요가 사라진다. 화면에서는 기존 "저장소 연결..." 버튼 하나를 두 갈래(이미 받아둔 폴더 / 원격에서 받기)로 넓히고, 주입된 대화상자 하나가 완성된 요청을 돌려준다. 원격 확인은 수동 버튼으로만 돈다.

**Tech Stack:** .NET Standard 2.0 + .NET Framework 4.8 (Core), WPF/MVVM (Vsix), LibGit2Sharp 0.32.0, System.Text.Json 10.0.3, NUnit 4 + Moq

**Spec:** `docs/superpowers/specs/2026-08-24-dbvc-git-workflow-design.md` — §3.11 "2차가 실제로 만드는 것 (2026-08-27 확정)", §7.2

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip, 컬럼명 포함. Core는 상태를 영어 식별자로 다루고 화면 계층에서만 한국어로 옮긴다. libgit2/서버의 영문 원문은 응답을 인용할 때만 그대로 싣는다.
- **주석은 "왜"만 적는다.** 한국어 평서문. 함정과 근거를 남기는 기존 문체를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): 원격 저장소를 도구 안에서 받는다`
- **테스트 이름은 영어** `Method_Result_WhenCondition` 형태다.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0`은 SSMS 21이 먼저 올리는 어셈블리에 맞춘 값이다. 올리면 어떤 DB에도 접속되지 않는다. LibGit2Sharp도 `0.32.0`에 고정이다.
- **테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다.** 전이 참조로만 받는다.
- **Git 인증은 SSH만 지원한다.** 폐쇄망 SSH 승인으로 HTTPS+PAT는 이번 범위에서 소멸했다. HTTPS 원격은 안내하고 거부한다.
- `dotnet test tests/DBVC.Core.Tests -f net10.0` 이 기본 실행 명령이다. `net48`은 Windows에서만 돈다. Vsix 테스트는 `-f net48`만 있다.
- **참조를 새로 더하지 않는다.** 이 계획은 이미 있는 LibGit2Sharp·WPF·Windows Forms만 쓴다. 참조가 바뀌면 `DBVC.Vsix.csproj`의 `IncludeCoreDependenciesInVsix` 목록을 `DBVC.Core.dll`의 AssemblyRef 폐포에서 다시 계산해야 한다.

## 실측으로 확정한 전제

리플렉션으로 직접 확인했다(LibGit2Sharp 0.32.0). 추측이 아니다.

- `CloneOptions` = `{ IsBare, Checkout, BranchName, RecurseSubmodules, OnCheckoutProgress, FetchOptions }`. `new CloneOptions()`의 `FetchOptions`는 **null이 아니다**. `Checkout` 기본값은 `true`.
- 자격 증명과 전송 진행률은 **중첩된 `FetchOptions`** 쪽에 있다. `CloneOptions`에는 없다.
- `TransferProgressHandler` → **`bool` 반환**(false면 전송 중단). `CheckoutProgressHandler` → **`void`**. 즉 **받는 동안에만 취소되고 펼치는 동안에는 취소되지 않는다.**
- `Commands.Fetch(Repository, string remoteName, IEnumerable<string> refspecs, FetchOptions, string logMessage)`
- `BranchTrackingDetails.AheadBy` / `BehindBy` 는 `int?` 이다.
- `LibGit2Sharp.UserCancelledException` 이 존재한다. 콜백이 false를 반환하면 이것이 나온다.
- **`Repository.Clone`의 반환값은 작업 트리가 아니라 `.git` 디렉터리 경로다**(내부적으로 `git_repository_path`를 반환한다). 그대로 매핑에 넣으면 이후 모든 동작이 어긋난다.

## 파일 구조

| 파일 | 책임 | 상태 |
|---|---|---|
| `src/DBVC.Core/RemoteUrlNaming.cs` | 원격 주소에서 받을 폴더 이름을 뽑는 순수 함수 | 신규 |
| `src/DBVC.Core/Models/CloneProgress.cs` | clone이 어느 단계에서 어디까지 왔는지 | 신규 |
| `src/DBVC.Core/Models/RemoteStatus.cs` | 원격 대비 앞섬·뒤처짐 | 신규 |
| `src/DBVC.Core/Abstractions.cs` | `IGitManager`에 두 메서드 추가 | 수정 |
| `src/DBVC.Core/GitManager.cs` | `CloneRepository`·`FetchRemoteStatus` 구현 | 수정 |
| `src/DBVC.Vsix/Services/IRepositoryConnectDialog.cs` | 저장소 연결 요청을 받는 이음매와 요청 모델 | 신규 |
| `src/DBVC.Vsix/Services/RepositoryConnectDialogAdapter.cs` | 실제 대화상자를 띄우는 구현 | 신규 |
| `src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml(.cs)` | 두 갈래 입력의 WPF 화면 | 신규 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 두 갈래 분기, clone 배선, 원격 확인 | 수정 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | "원격 확인" 버튼과 상태 표시 | 수정 |
| `tests/DBVC.Core.Tests/RemoteUrlNamingTests.cs` | 폴더 이름 규칙 | 신규 |
| `tests/DBVC.Core.Tests/GitManagerTests.cs` | clone·fetch 실동작(파일 경로 원격) | 수정 |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | 두 갈래 분기·진행·취소·원격 확인 | 수정 |
| `tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs` | 생성자 인자 교체에 따른 수정 | 수정 |

---

### Task 1: 원격 주소에서 받을 폴더 이름을 뽑는다

**Files:**
- Create: `src/DBVC.Core/RemoteUrlNaming.cs`
- Test: `tests/DBVC.Core.Tests/RemoteUrlNamingTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public static class DBVC.Core.RemoteUrlNaming` / `public static string? SuggestFolderName(string? remoteUrl)`

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/RemoteUrlNamingTests.cs` 를 새로 만든다.

```csharp
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 사용자가 붙여 넣는 것은 GitHub·GitLab이 알려주는 Clone URL이다.
    /// 그 문자열에서 폴더 이름을 뽑는 규칙은 네트워크 없이 전량 고정할 수 있다.
    /// </summary>
    [TestFixture]
    public class RemoteUrlNamingTests
    {
        [Test]
        public void SuggestFolderName_ReturnsTheRepositoryName_WhenScpFormUrl()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@github.com:org/db-schema-sales.git"),
                Is.EqualTo("db-schema-sales"));
        }

        [Test]
        public void SuggestFolderName_ReturnsTheRepositoryName_WhenSshUrlHasPortAndNestedGroups()
        {
            // GitLab이 비표준 SSH 포트를 쓰면 Clone 버튼이 이 형태를 내준다.
            Assert.That(RemoteUrlNaming.SuggestFolderName("ssh://git@gitlab.corp:2222/db/team/db-schema.git"),
                Is.EqualTo("db-schema"));
        }

        [Test]
        public void SuggestFolderName_ReturnsTheRepositoryName_WhenScpFormHasNoPathSeparator()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@host:db-schema"), Is.EqualTo("db-schema"));
        }

        [Test]
        public void SuggestFolderName_DropsTheGitSuffix_InAnyCase()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@host:org/Sales.GIT"), Is.EqualTo("Sales"));
        }

        [Test]
        public void SuggestFolderName_IgnoresTrailingSeparators()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName("ssh://git@host/org/db-schema.git/"),
                Is.EqualTo("db-schema"));
        }

        [Test]
        public void SuggestFolderName_ReturnsNull_WhenUrlIsEmpty()
        {
            Assert.That(RemoteUrlNaming.SuggestFolderName(null), Is.Null);
            Assert.That(RemoteUrlNaming.SuggestFolderName("   "), Is.Null);
        }

        [Test]
        public void SuggestFolderName_ReturnsNull_WhenTheNameWouldNotBeAValidFolderName()
        {
            // 제안을 못 하는 것과 못 만들 이름을 제안하는 것은 다르다.
            // 후자는 사용자가 확인을 누른 뒤에야 실패한다.
            Assert.That(RemoteUrlNaming.SuggestFolderName("git@host:org/a|b.git"), Is.Null);
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RemoteUrlNamingTests"`
Expected: 컴파일 실패 — `RemoteUrlNaming`이 없다

- [x] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/RemoteUrlNaming.cs`:

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격 주소에서 받을 폴더 이름을 제안한다. 순수 함수만 두어 네트워크 없이 전량 테스트한다.
    ///
    /// 제안일 뿐 강제가 아니다 — 사용자가 대화상자에서 고칠 수 있다. 그래서 판정하지 못하는
    /// 입력에는 억지로 이름을 만들어 내지 않고 null을 돌려준다.
    /// </summary>
    public static class RemoteUrlNaming
    {
        private const string GitSuffix = ".git";

        /// <summary>
        /// Windows가 폴더 이름에 허용하지 않는 문자.
        ///
        /// <see cref="System.IO.Path.GetInvalidFileNameChars"/>를 쓰지 않는다 — 그 값은 실행 중인 OS가
        /// 정해서 Unix에서는 '\0'과 '/'만 돌려준다. 같은 입력이 플랫폼마다 다른 답을 내면
        /// Linux에서 도는 CI가 Windows에서 통과한 테스트를 떨어뜨린다. DBVC가 실제로 도는
        /// 곳은 언제나 Windows이므로 그쪽 규칙을 고정해 박는다.
        /// </summary>
        private static readonly char[] InvalidFolderNameChars =
            { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        public static string? SuggestFolderName(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) return null;

            var trimmed = remoteUrl!.Trim().TrimEnd('/', '\\');
            if (trimmed.Length == 0) return null;

            // scp 형식(git@host:org/name)은 콜론이, URL 형식은 슬래시가 마지막 구분자다.
            // 둘을 함께 보면 형식을 먼저 판정하지 않아도 된다.
            var cut = trimmed.LastIndexOfAny(new[] { '/', '\\', ':' });
            var name = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;

            if (name.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - GitSuffix.Length);
            }

            if (name.Length == 0) return null;

            // 못 만들 이름을 제안하면 사용자가 확인을 누른 뒤에야 실패한다.
            if (name.IndexOfAny(InvalidFolderNameChars) >= 0) return null;

            return name;
        }
    }
}
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~RemoteUrlNamingTests"`
Expected: 7개 PASS

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/RemoteUrlNaming.cs tests/DBVC.Core.Tests/RemoteUrlNamingTests.cs
git commit -m "feat(core): 원격 주소에서 받을 폴더 이름을 뽑는다"
```

---

### Task 2: 원격 저장소를 받는다 (성공 경로)

**Files:**
- Create: `src/DBVC.Core/Models/CloneProgress.cs`
- Modify: `src/DBVC.Core/Abstractions.cs` (`IGitManager`)
- Modify: `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public enum DBVC.Core.Models.ClonePhase { Transferring = 0, CheckingOut = 1 }`
  - `public sealed class DBVC.Core.Models.CloneProgress` — 생성자 `CloneProgress(ClonePhase phase, int completed, int total)`, 속성 `Phase`·`Completed`·`Total`
  - `IGitManager.CloneRepository(string remoteUrl, string targetPath, IProgress<CloneProgress>? progress, CancellationToken cancellationToken)` → `string` (작업 트리 경로)

- [x] **Step 1: 진행 모델을 만든다**

`src/DBVC.Core/Models/CloneProgress.cs`:

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// clone의 단계. 화면이 이것을 알아야 하는 이유는 진행률 문구가 아니라 취소 버튼이다 —
    /// libgit2의 CheckoutProgressHandler는 void라 펼치는 단계는 끊을 수 없다.
    /// </summary>
    public enum ClonePhase
    {
        /// <summary>원격에서 객체를 받는 중. 취소가 실제로 걸리는 유일한 단계다.</summary>
        Transferring = 0,

        /// <summary>받은 것을 작업 트리에 펼치는 중.</summary>
        CheckingOut = 1
    }

    /// <summary>clone이 어느 단계에서 어디까지 왔는지.</summary>
    public sealed class CloneProgress
    {
        public CloneProgress(ClonePhase phase, int completed, int total)
        {
            Phase = phase;
            Completed = completed;
            Total = total;
        }

        public ClonePhase Phase { get; }

        /// <summary>받은 객체 수 또는 펼친 단계 수.</summary>
        public int Completed { get; }

        /// <summary>전체 수. 원격이 알려주기 전에는 0일 수 있다.</summary>
        public int Total { get; }
    }
}
```

- [x] **Step 2: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs` 의 클래스 안, 파일 하단의 `private sealed class` 들 앞에 더한다. 파일 상단 `using` 에 `using DBVC.Core.Models;` 가 있는지 확인하고 없으면 더한다.

```csharp
        // ---------- Clone ----------

        /// <summary>
        /// 보고를 그 자리에서 모은다. Progress&lt;T&gt;는 생성된 스레드의 SynchronizationContext로
        /// 넘기는데, 테스트 스레드에는 그것이 없어 순서와 시점이 보장되지 않는다.
        /// </summary>
        private sealed class RecordingProgress<T> : IProgress<T>
        {
            private readonly Action<T>? _onReport;
            public RecordingProgress(Action<T>? onReport = null) { _onReport = onReport; }
            public System.Collections.Generic.List<T> Reports { get; } = new System.Collections.Generic.List<T>();
            public void Report(T value) { Reports.Add(value); _onReport?.Invoke(value); }
        }

        [Test]
        public void CloneRepository_CreatesAWorkingTreeAtTheTargetPath_WhenTheRemoteIsReachable()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();

            var result = new GitManager().CloneRepository(originPath, targetPath, null, CancellationToken.None);

            // Repository.Clone이 돌려주는 것은 .git 디렉터리 경로다. 그것을 그대로 매핑에 넣으면
            // 이후 모든 동작이 어긋나므로, 반환값은 작업 트리여야 한다.
            Assert.That(result, Is.EqualTo(targetPath));
            Assert.That(File.Exists(Path.Combine(targetPath, "dbo", "Tables", "Users.sql")), Is.True);
            Assert.That(new GitManager().IsRepository(targetPath), Is.True);
        }

        [Test]
        public void CloneRepository_SetsUpstreamTracking_WhenCloning()
        {
            // clone이 Init+Remote+upstream을 대신하는 근거다. 이것이 깨지면 첫 Push가
            // "추적 중인 원격 브랜치가 없어" 로 거부된다.
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();

            new GitManager().CloneRepository(originPath, targetPath, null, CancellationToken.None);

            using var cloned = new Repository(targetPath);
            Assert.That(cloned.Head.IsTracking, Is.True);
        }

        [Test]
        public void CloneRepository_ReportsProgress_WhileCloning()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();
            var progress = new RecordingProgress<CloneProgress>();

            new GitManager().CloneRepository(originPath, targetPath, progress, CancellationToken.None);

            // 파일 경로 원격은 libgit2의 local 전송을 타서 전송 단계 보고가 없을 수 있다.
            // 그래서 여기서 고정하는 것은 checkout 보고뿐이고, 전송 보고는 실기 확인 목록에 있다.
            Assert.That(progress.Reports, Is.Not.Empty);
            Assert.That(progress.Reports.Exists(p => p.Phase == ClonePhase.CheckingOut), Is.True);
        }
```

- [x] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CloneRepository"`
Expected: 컴파일 실패 — `CloneRepository`가 없다

- [x] **Step 4: 인터페이스를 넓힌다**

`src/DBVC.Core/Abstractions.cs` 의 `IGitManager` 안, `IsRepository` 바로 아래에 더한다. 파일 상단에 `using System;`, `using System.Threading;`, `using DBVC.Core.Models;` 가 있는지 확인하고 없으면 더한다.

```csharp
        /// <summary>
        /// 원격 저장소를 <paramref name="targetPath"/>에 받고 그 작업 트리 경로를 반환한다.
        ///
        /// 매핑이 생기기 전에 일어나므로 다른 API와 달리 (serverName, databaseName)을 받지 않는다.
        /// <paramref name="targetPath"/>는 <b>없는 폴더</b>여야 한다 — 이 제약이 있어야
        /// 실패·취소 뒤처리에서 "지워도 되는 폴더"를 판별할 필요가 없다.
        /// </summary>
        /// <param name="cancellationToken">
        /// 취소되면 <see cref="OperationCanceledException"/>이 전파되고 만든 폴더는 지워진다.
        /// 다만 취소가 즉시 걸리는 것은 받는 동안뿐이다 — libgit2의 checkout 콜백은 중단을 받지 않는다.
        /// </param>
        string CloneRepository(
            string remoteUrl,
            string targetPath,
            IProgress<CloneProgress>? progress,
            CancellationToken cancellationToken);
```

- [x] **Step 5: 구현한다**

`src/DBVC.Core/GitManager.cs` 의 `IsRepository` 아래에 더한다. 파일 상단 `using` 에 `using System.Threading;` 을 더한다.

```csharp
        /// <summary>
        /// 원격 저장소를 받는다. (설계 3.11)
        /// </summary>
        public string CloneRepository(
            string remoteUrl,
            string targetPath,
            IProgress<CloneProgress>? progress,
            CancellationToken cancellationToken)
        {
            var options = new CloneOptions
            {
                OnCheckoutProgress = (path, completed, total) =>
                    progress?.Report(new CloneProgress(ClonePhase.CheckingOut, completed, total))
            };

            // 자격 증명과 전송 진행률은 CloneOptions가 아니라 그 안의 FetchOptions에 있다.
            // new CloneOptions()가 FetchOptions를 이미 채워 주므로 그대로 쓴다(실측 확인).
            options.FetchOptions.OnTransferProgress = transfer =>
            {
                progress?.Report(new CloneProgress(
                    ClonePhase.Transferring, transfer.ReceivedObjects, transfer.TotalObjects));

                // false를 반환하면 libgit2가 전송을 끊고 UserCancelledException을 낸다.
                // 취소가 즉시 걸리는 유일한 자리다.
                return !cancellationToken.IsCancellationRequested;
            };
            options.FetchOptions.CredentialsProvider =
                (url, usernameFromUrl, types) => ResolveCredentials(types, out _);

            Repository.Clone(remoteUrl, targetPath, options);

            // Repository.Clone의 반환값은 .git 디렉터리 경로다. 매핑에 들어갈 것은 작업 트리다.
            return targetPath;
        }
```

- [x] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CloneRepository"`
Expected: 3개 PASS

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Core/Models/CloneProgress.cs src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 원격 저장소를 도구 안에서 받는다"
```

---

### Task 3: 받을 수 없는 요청을 시작 전에 거부한다

**Files:**
- Modify: `src/DBVC.Core/GitManager.cs` (`CloneRepository` 본문 앞 가드, private 헬퍼 `IsSshAvailableWithoutRepository` 추가)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: Task 2의 `CloneRepository`
- Produces: `GitManager` private `static bool IsSshAvailableWithoutRepository()` — Task 4의 실패 안내가 같은 것을 쓴다

> `IsSshAvailableWithoutRepository`는 기계의 전역 git config를 읽으므로 단위 테스트로 고정하지
> 않는다 — 값을 무엇으로 단정해도 다른 개발 기계에서 틀린다. 대신 완료 조건의 실기 확인이 덮는다.

- [x] **Step 1: 실패하는 테스트를 쓴다**

Task 2가 더한 `// ---------- Clone ----------` 절 뒤에 이어 쓴다.

```csharp
        [Test]
        public void CloneRepository_Refuses_WhenTheTargetFolderAlreadyExists()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();
            Directory.CreateDirectory(targetPath);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new GitManager().CloneRepository(originPath, targetPath, null, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain(targetPath),
                "어느 폴더가 문제인지 경로로 알려줘야 합니다");
            Assert.That(ex.Message, Does.Contain("이미"));
        }

        [Test]
        public void CloneRepository_RefusesBeforeCreatingAnything_WhenTheRemoteIsHttps()
        {
            var targetPath = NewTempDir();

            var ex = Assert.Throws<GitAuthenticationException>(
                () => new GitManager().CloneRepository(
                    "https://example.invalid/org/x.git", targetPath, null, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("SSH"),
                "HTTPS 원격에는 SSH로 바꾸는 방법을 안내해야 합니다");
            Assert.That(Directory.Exists(targetPath), Is.False,
                "네트워크를 타기 전에 거부해야 합니다 - 폴더가 남으면 다음 시도가 '이미 있음'으로 막힙니다");
        }

        [Test]
        public void CloneRepository_Refuses_WhenTheRemoteUrlIsEmpty()
        {
            Assert.Throws<ArgumentException>(
                () => new GitManager().CloneRepository("  ", NewTempDir(), null, CancellationToken.None));
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CloneRepository_Refuses"`
Expected: FAIL — 가드가 없어 `Repository.Clone`의 영문 예외가 나온다

- [x] **Step 3: 가드를 더한다**

`CloneRepository` 본문 맨 위, `var options = ...` 앞에 넣는다.

```csharp
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                throw new ArgumentException("원격 주소가 비어 있습니다.", nameof(remoteUrl));
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("받을 폴더 경로가 비어 있습니다.", nameof(targetPath));
            }

            // HTTPS는 네트워크를 타기 전에 거른다. 자격 증명 콜백까지 흘려보내면 libgit2의
            // 영문 원문이 먼저 나오고, 그 뒤에 붙이는 안내는 이미 늦다.
            if (RemoteDiagnostics.Classify(remoteUrl) == RemoteUrlKind.Https)
            {
                throw new GitAuthenticationException(
                    RemoteDiagnostics.Explain(remoteUrl, IsSshAvailableWithoutRepository())!);
            }

            // 있는 폴더에 받으면 "이 폴더를 내가 만들었나"를 기억해야 하고, 그 기억이 틀리는 날
            // 남의 폴더를 지운다. 없는 폴더만 받으면 그 판별 자체가 필요 없다.
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                throw new InvalidOperationException(
                    $"'{targetPath}'에 이미 무언가 있습니다. 아직 없는 폴더 경로를 지정하세요.");
            }
```

같은 파일의 private 헬퍼들 옆에 더한다.

```csharp
        /// <summary>
        /// 저장소 없이 ssh 사용 가능 여부를 본다.
        ///
        /// Pull·Push는 repo.Config에서 core.sshCommand도 함께 보지만 clone 시점에는 저장소가
        /// 아직 없다. Configuration.BuildFrom(null)이 전역·시스템 config를 열어 주므로
        /// 같은 판정이 나온다 — OpenSSH 선택적 기능이 꺼져 있어도 Git for Windows의 ssh.exe를
        /// core.sshCommand로 가리키는 구성(사내 PC에서 흔함)은 실제로 SSH가 된다.
        /// 이것을 빠뜨리면 그런 PC에서 "OpenSSH 클라이언트를 설치하세요"라는 틀린 안내가 나간다.
        /// </summary>
        private static bool IsSshAvailableWithoutRepository()
        {
            if (SshExecutableLocator.IsAvailable()) return true;

            try
            {
                using var config = Configuration.BuildFrom(null);
                return !string.IsNullOrWhiteSpace(config.Get<string>("core.sshCommand")?.Value);
            }
            catch (Exception ex)
            {
                // config를 못 읽는 것이 clone 실패의 원인은 아니다. 안내만 덜 정확해진다.
                Debug.WriteLine($"GitManager.IsSshAvailableWithoutRepository failed: {ex.Message}");
                return false;
            }
        }
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CloneRepository"`
Expected: 6개 PASS

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "fix(core): 받을 수 없는 clone 요청을 시작 전에 거부한다"
```

---

### Task 4: 실패·취소하면 만든 폴더를 지운다

**Files:**
- Modify: `src/DBVC.Core/GitManager.cs` (`CloneRepository` 본문, private 헬퍼 추가)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: Task 2·3의 `CloneRepository`
- Produces: 없음

- [x] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Test]
        public void CloneRepository_RemovesTheFolderItCreated_WhenTheRemoteDoesNotExist()
        {
            // 절반만 받아진 폴더가 남으면 다음 시도가 '이미 있음'으로 막힌다.
            var missingOrigin = Path.Combine(Path.GetTempPath(), "dbvc_no_such_" + Guid.NewGuid().ToString("N"));
            var targetPath = NewTempDir();

            Assert.Throws<GitRemoteException>(
                () => new GitManager().CloneRepository(missingOrigin, targetPath, null, CancellationToken.None));

            Assert.That(Directory.Exists(targetPath), Is.False);
        }

        [Test]
        public void CloneRepository_RemovesTheFolderItCreated_WhenCancelledWhileRunning()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();
            using var cts = new CancellationTokenSource();

            // 첫 보고에서 취소한다. 미리 취소된 토큰을 넘기면 폴더가 만들어지기도 전에 끝나
            // 정리 경로를 지나가지 않는다.
            var progress = new RecordingProgress<CloneProgress>(_ => cts.Cancel());

            Assert.Throws<OperationCanceledException>(
                () => new GitManager().CloneRepository(originPath, targetPath, progress, cts.Token));

            Assert.That(Directory.Exists(targetPath), Is.False);
        }

        [Test]
        public void CloneRepository_ThrowsCancellation_WhenTheTokenIsAlreadyCancelled()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => new GitManager().CloneRepository(originPath, targetPath, null, cts.Token));

            Assert.That(Directory.Exists(targetPath), Is.False);
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CloneRepository_Removes"`
Expected: FAIL — libgit2의 원본 예외가 나오고 폴더가 남는다

- [x] **Step 3: 구현한다**

`CloneRepository`의 `Repository.Clone(...)` 호출과 `return` 을 아래로 바꾼다. 가드 블록과 `options` 조립은 그대로 둔다.

```csharp
            // 취소가 이미 걸려 있으면 폴더를 만들지도 않는다.
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Repository.Clone(remoteUrl, targetPath, options);

                // checkout 단계는 libgit2가 중단을 받지 않으므로, 그 사이에 눌린 취소는
                // 여기서 처리한다. 사용자가 취소를 눌렀는데 저장소가 남아 있으면
                // 다음 시도가 '이미 있음'으로 막혀 무엇을 해야 하는지 알 수 없게 된다.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                // 우리가 만든 폴더만 존재할 수 있다(위 가드가 보장한다). 지워도 안전하다.
                DeleteDirectoryTree(targetPath);

                if (ex is OperationCanceledException) throw;

                // 콜백이 false를 반환해 끊긴 경우다. 실패가 아니라 사용자가 누른 것이다.
                if (ex is UserCancelledException)
                {
                    throw new OperationCanceledException("원격 저장소 받기를 취소했습니다.", ex, cancellationToken);
                }

                var guidance = RemoteDiagnostics.Explain(remoteUrl, IsSshAvailableWithoutRepository());
                var message = guidance == null
                    ? ex.Message
                    : ex.Message + Environment.NewLine + Environment.NewLine + guidance;

                throw new GitRemoteException(message, ex);
            }

            // Repository.Clone의 반환값은 .git 디렉터리 경로다. 매핑에 들어갈 것은 작업 트리다.
            return targetPath;
```

같은 파일의 private 헬퍼들 옆에 더한다.

```csharp
        /// <summary>
        /// 폴더를 통째로 지운다. .git 내부에는 읽기 전용 파일(pack 등)이 있어
        /// 속성을 먼저 풀지 않으면 Directory.Delete가 거부된다.
        ///
        /// 지우지 못해도 예외를 밖으로 내지 않는다 — 호출자는 이미 실패를 보고하는 중이고,
        /// 정리 실패로 원래 원인이 가려지면 사용자가 무엇을 고쳐야 하는지 알 수 없다.
        /// </summary>
        private static void DeleteDirectoryTree(string path)
        {
            if (!Directory.Exists(path)) return;

            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.DeleteDirectoryTree failed for '{path}': {ex.Message}");
            }
        }
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CloneRepository"`
Expected: 9개 PASS

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "fix(core): clone이 실패하거나 취소되면 만든 폴더를 지운다"
```

---

### Task 5: 원격이 얼마나 앞서 있는지 읽는다

**Files:**
- Create: `src/DBVC.Core/Models/RemoteStatus.cs`
- Modify: `src/DBVC.Core/Abstractions.cs` (`IGitManager`)
- Modify: `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: Task 2의 `CloneRepository`(테스트에서 로컬 클론을 만드는 데 쓴다)
- Produces:
  - `public sealed class DBVC.Core.Models.RemoteStatus` — 생성자 `RemoteStatus(int aheadBy, int behindBy)`, 속성 `AheadBy`·`BehindBy`
  - `IGitManager.FetchRemoteStatus(string serverName, string databaseName)` → `RemoteStatus`

- [x] **Step 1: 모델을 만든다**

`src/DBVC.Core/Models/RemoteStatus.cs`:

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// 원격을 읽어 본 결과. Fetch는 참조만 갱신하고 작업 트리를 건드리지 않으므로
    /// 이 값을 얻는 데 부수효과가 없다.
    /// </summary>
    public sealed class RemoteStatus
    {
        public RemoteStatus(int aheadBy, int behindBy)
        {
            AheadBy = aheadBy;
            BehindBy = behindBy;
        }

        /// <summary>원격에 없는 로컬 커밋 수. Push할 것.</summary>
        public int AheadBy { get; }

        /// <summary>로컬에 없는 원격 커밋 수. Pull할 것.</summary>
        public int BehindBy { get; }
    }
}
```

- [x] **Step 2: 실패하는 테스트를 쓴다**

`GitManagerTests.cs` 에 `// ---------- 원격 확인 ----------` 절을 만들어 더한다.

```csharp
        // ---------- 원격 확인 ----------

        [Test]
        public void FetchRemoteStatus_ReportsBehind_WhenTheRemoteHasNewCommits()
        {
            var originPath = NewRepoWithCommit();
            var localPath = NewTempDir();
            new GitManager().CloneRepository(originPath, localPath, null, CancellationToken.None);

            // 기존 헬퍼를 그대로 쓴다. 같은 일을 하는 것을 하나 더 만들면 둘 중 하나만 고쳐진다.
            CommitOneFile(originPath, "dbo/Views/V1.sql", "CREATE OR ALTER VIEW V1 AS SELECT 1 AS X;", "add view");

            var status = NewGitManager("localhost", "testdb", localPath)
                .FetchRemoteStatus("localhost", "testdb");

            Assert.That(status.BehindBy, Is.EqualTo(1));
            Assert.That(status.AheadBy, Is.EqualTo(0));
        }

        [Test]
        public void FetchRemoteStatus_ReportsAhead_WhenLocalHasUnpushedCommits()
        {
            var originPath = NewRepoWithCommit();
            var localPath = NewTempDir();
            new GitManager().CloneRepository(originPath, localPath, null, CancellationToken.None);

            CommitOneFile(localPath, "dbo/Views/V2.sql", "CREATE OR ALTER VIEW V2 AS SELECT 2 AS X;", "local only");

            var status = NewGitManager("localhost", "testdb", localPath)
                .FetchRemoteStatus("localhost", "testdb");

            Assert.That(status.AheadBy, Is.EqualTo(1));
            Assert.That(status.BehindBy, Is.EqualTo(0));
        }

        [Test]
        public void FetchRemoteStatus_ExplainsInKorean_WhenTheCurrentBranchHasNoUpstream()
        {
            // Pull·Push와 글자 그대로 같은 검사를 재사용하는지 고정한다.
            // 복제본을 만들면 한쪽 문구만 고쳐지는 일이 실제로 일어난다.
            var originPath = NewRepoWithCommit();
            var localPath = NewRepoWithCommit();
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", originPath);
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<InvalidOperationException>(() => git.FetchRemoteStatus("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("추적"));
            Assert.That(ex.Message, Does.Not.Contain("tracking information"));
        }

        [Test]
        public void FetchRemoteStatus_Throws_WhenTheDatabaseIsNotMapped()
        {
            var git = NewGitManager("localhost", "other", NewRepoWithCommit());

            Assert.Throws<InvalidOperationException>(() => git.FetchRemoteStatus("localhost", "testdb"));
        }
```

- [x] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~FetchRemoteStatus"`
Expected: 컴파일 실패 — `FetchRemoteStatus`가 없다

- [x] **Step 4: 인터페이스를 넓힌다**

`src/DBVC.Core/Abstractions.cs` 의 `IGitManager`, `HasCommitsToPush` 아래에 더한다.

```csharp
        /// <summary>
        /// 원격을 받아 앞섬·뒤처짐을 센다. 참조만 갱신하고 작업 트리는 건드리지 않는다.
        /// 매핑이 없거나 원격·추적 브랜치가 없으면 한국어 안내를 담은 예외를 던진다.
        /// </summary>
        RemoteStatus FetchRemoteStatus(string serverName, string databaseName);
```

- [x] **Step 5: 구현한다**

`src/DBVC.Core/GitManager.cs` 의 `HasCommitsToPush` 아래에 더한다.

```csharp
        /// <summary>
        /// 원격 상태를 부수효과 없이 읽는다. (설계 3.11)
        ///
        /// 수동 버튼으로만 불린다. 새로고침에 붙이면 응답 없는 원격이 변경 목록을 보는 일까지
        /// 느리게 만든다.
        /// </summary>
        public RemoteStatus FetchRemoteStatus(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null)
            {
                throw new InvalidOperationException(
                    $"'{serverName}.{databaseName}'에 연결된 Git 저장소가 없어 원격을 확인할 수 없습니다.");
            }

            using var repo = new Repository(repoPath);

            // 원격 없음·추적 브랜치 없음의 안내는 Pull·Push가 쓰는 것을 그대로 쓴다.
            var guidance = ValidateRemoteAndBuildGuidance(repo, repoPath, "원격 확인");
            var remoteName = repo.Head.RemoteName;

            try
            {
                var fetchOptions = new FetchOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) => ResolveCredentials(types, out _)
                };

                // 빈 refspec은 "원격에 설정된 기본 refspec을 쓰라"는 뜻이다.
                Commands.Fetch(repo, remoteName, Array.Empty<string>(), fetchOptions, null);
            }
            // Pull·Push와 같은 모양으로 좀힌다. 안내할 것이 있을 때만 가로채고,
            // 없으면 원본 예외를 그대로 흘려보낸다 — 모든 예외를 감싸면 코딩 실수까지
            // "원격과 통신하지 못했다"로 둔갑해서 원인을 찾을 수 없게 된다.
            catch (LibGit2SharpException ex) when (guidance != null)
            {
                throw new GitRemoteException(
                    ex.Message + Environment.NewLine + Environment.NewLine + guidance, ex);
            }

            var details = repo.Head.TrackingDetails;
            return new RemoteStatus(details.AheadBy ?? 0, details.BehindBy ?? 0);
        }
```

- [x] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~FetchRemoteStatus"`
Expected: 4개 PASS

- [x] **Step 7: 전체 Core 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: 전부 PASS 또는 Skip

- [x] **Step 8: 커밋**

```bash
git add src/DBVC.Core/Models/RemoteStatus.cs src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 원격이 얼마나 앞서 있는지 부수효과 없이 읽는다"
```

---

### Task 6: 저장소 연결을 두 갈래로 넓힌다

**Files:**
- Create: `src/DBVC.Vsix/Services/IRepositoryConnectDialog.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:35`(필드), `:75`(생성자 인자), `:94`(대입), `:843-864`(`ConnectRepository`)
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`
- Modify: `tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs:29,61`

**Interfaces:**
- Consumes: `IGitManager.CloneRepository`(Task 2~4)
- Produces:
  - `public enum DBVC.Vsix.Services.RepositoryConnectKind { ExistingFolder = 0, Clone = 1 }`
  - `public sealed class RepositoryConnectRequest` — `static ForExistingFolder(string path)`, `static ForClone(string remoteUrl, string targetPath)`, 속성 `Kind`·`ExistingPath`·`RemoteUrl`·`TargetPath`
  - `public interface IRepositoryConnectDialog` — `RepositoryConnectRequest? Prompt(string serverName, string databaseName)`
  - `ViewChangesViewModel` 생성자 8번째 인자가 `IFolderBrowseDialog? folderDialog` → `IRepositoryConnectDialog? connectDialog` 로 바뀐다
  - `ViewChangesViewModel` private: `ConnectExistingFolder(string)`, `AdoptRepository(string)`, `CloneAndConnect(string, string)`

- [x] **Step 1: 이음매를 만든다**

`src/DBVC.Vsix/Services/IRepositoryConnectDialog.cs`:

```csharp
namespace DBVC.Vsix.Services
{
    /// <summary>사용자가 고른 연결 방식.</summary>
    public enum RepositoryConnectKind
    {
        /// <summary>이미 받아둔 폴더를 그대로 쓴다.</summary>
        ExistingFolder = 0,

        /// <summary>원격에서 새로 받는다.</summary>
        Clone = 1
    }

    /// <summary>
    /// 저장소를 연결해 달라는 완성된 요청. 대화상자가 어떻게 생겼는지를 ViewModel에서 감춘다.
    /// </summary>
    public sealed class RepositoryConnectRequest
    {
        private RepositoryConnectRequest(RepositoryConnectKind kind, string? existingPath, string? remoteUrl, string? targetPath)
        {
            Kind = kind;
            ExistingPath = existingPath;
            RemoteUrl = remoteUrl;
            TargetPath = targetPath;
        }

        public static RepositoryConnectRequest ForExistingFolder(string path) =>
            new RepositoryConnectRequest(RepositoryConnectKind.ExistingFolder, path, null, null);

        public static RepositoryConnectRequest ForClone(string remoteUrl, string targetPath) =>
            new RepositoryConnectRequest(RepositoryConnectKind.Clone, null, remoteUrl, targetPath);

        public RepositoryConnectKind Kind { get; }

        /// <summary><see cref="RepositoryConnectKind.ExistingFolder"/>일 때만 값이 있다.</summary>
        public string? ExistingPath { get; }

        /// <summary><see cref="RepositoryConnectKind.Clone"/>일 때만 값이 있다.</summary>
        public string? RemoteUrl { get; }

        /// <summary><see cref="RepositoryConnectKind.Clone"/>일 때만 값이 있다. 아직 없는 폴더 경로다.</summary>
        public string? TargetPath { get; }
    }

    /// <summary>
    /// 저장소 연결 방식을 사용자에게 묻는다. ViewModel이 대화상자 구현에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IRepositoryConnectDialog
    {
        /// <summary>사용자가 취소하면 <c>null</c>.</summary>
        RepositoryConnectRequest? Prompt(string serverName, string databaseName);
    }
}
```

- [x] **Step 2: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`:

1. 파일 하단(약 2302행)의 `RecordingFolderDialog` 를 아래로 **교체**한다.

```csharp
        private sealed class RecordingConnectDialog : IRepositoryConnectDialog
        {
            public RepositoryConnectRequest? RequestToReturn { get; set; }
            public int CallCount { get; private set; }

            public RepositoryConnectRequest? Prompt(string serverName, string databaseName)
            {
                CallCount++;
                return RequestToReturn;
            }
        }
```

2. 필드 선언(약 28행)을 바꾼다.

```csharp
        private RecordingConnectDialog _connectDialog = null!;
```

3. `SetUp`(약 50행)의 `_folderDialog = new RecordingFolderDialog();` 를 바꾼다.

```csharp
            _connectDialog = new RecordingConnectDialog();
```

4. ViewModel을 만드는 다섯 자리(약 95·296·581·600·1787행)에서 인자 `_folderDialog` 를 `_connectDialog` 로 바꾼다.

5. 기존 세 테스트(약 869·881·896행)를 아래로 바꾼다.

```csharp
        [Test]
        public void ConnectRepositoryCommand_SavesTheMapping_WhenTheChosenFolderIsAGitRepository()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _git.Setup(g => g.IsRepository(@"C:\chosen-repo")).Returns(true);
            _connectDialog.RequestToReturn = RepositoryConnectRequest.ForExistingFolder(@"C:\chosen-repo");
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(Server, Database, @"C:\chosen-repo"), Times.Once);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNothing_WhenTheUserCancels()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn = null;
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(_connectDialog.CallCount, Is.EqualTo(1));
            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Empty, "취소는 오류가 아닙니다");
        }

        [Test]
        public void ConnectRepositoryCommand_RefusesAFolderThatIsNotAGitRepository()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _git.Setup(g => g.IsRepository(It.IsAny<string>())).Returns(false);
            _connectDialog.RequestToReturn = RepositoryConnectRequest.ForExistingFolder(@"C:\not-a-repo");
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Not.Empty,
                "유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패합니다");
        }
```

6. `tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs` 의 두 자리(29·61행)에서 `Mock.Of<IFolderBrowseDialog>()` 를 `Mock.Of<IRepositoryConnectDialog>()` 로 바꾼다.

- [x] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ConnectRepositoryCommand"`
Expected: 컴파일 실패 — 생성자가 아직 `IFolderBrowseDialog`를 받는다

- [x] **Step 4: ViewModel을 고친다**

35행의 필드를 바꾼다.

```csharp
        private readonly IRepositoryConnectDialog _connectDialog;
```

75행의 생성자 인자를 바꾼다. **자리를 옮기지 않는다** — 위치 인자로 넘기는 호출부가 있어 순서가 바뀌면 조용히 다른 곳에 꽂힌다.

```csharp
            IRepositoryConnectDialog? connectDialog = null,
```

94행의 대입을 바꾼다. 실제 구현은 Task 9에서 붙이므로 **이 단계에서는 아래를 쓴다.**

```csharp
            // 실제 대화상자는 Task 9에서 붙인다. 그때까지의 기본값은 무동작이다 —
            // 여기서 던지면 connectDialog를 넘기지 않는 DbvcServices.CreateViewChangesViewModel이
            // 죽어서 도구 창 자체가 열리지 않는다.
            _connectDialog = connectDialog ?? new NoOpRepositoryConnectDialog();
```

같은 파일의 private 중첩 클래스들 옆에 더한다. **Task 9에서 지운다.**

```csharp
        /// <summary>
        /// Task 9이 실제 대화상자를 붙이기 전까지의 기본값. 언제나 취소로 답한다.
        /// 셸 밖 실행과 대화상자를 넘기지 않는 조립 경로가 죽지 않게 하는 것이 전부다.
        /// </summary>
        private sealed class NoOpRepositoryConnectDialog : IRepositoryConnectDialog
        {
            public RepositoryConnectRequest? Prompt(string serverName, string databaseName) => null;
        }
```

`ConnectRepository`(843~864행)를 통째로 바꾼다.

```csharp
        private void ConnectRepository()
        {
            if (!CanConnectRepository()) return;

            var request = _connectDialog.Prompt(ServerName!, DatabaseName!);

            // 사용자가 취소한 경우다. 오류가 아니다.
            if (request == null) return;

            if (request.Kind == RepositoryConnectKind.ExistingFolder)
            {
                ConnectExistingFolder(request.ExistingPath!);
                return;
            }

            CloneAndConnect(request.RemoteUrl!, request.TargetPath!);
        }

        private void ConnectExistingFolder(string path)
        {
            if (!_gitManager.IsRepository(path))
            {
                // 유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패한다.
                _notifier.ShowError("DBVC",
                    $"'{path}'은(는) Git 저장소가 아닙니다. 이미 받아둔 저장소 폴더를 고르거나 원격에서 받으세요.");
                return;
            }

            AdoptRepository(path);
        }

        /// <summary>
        /// 매핑을 저장하고 화면을 새 저장소 기준으로 다시 판정한다.
        /// 두 갈래가 끝나는 자리가 같아야 한쪽만 갱신을 빠뜨리는 일이 없다.
        /// </summary>
        private void AdoptRepository(string path)
        {
            _configManager.AddMapping(ServerName!, DatabaseName!, path);

            // 매핑이 생겼으므로 상태를 다시 판정한다. 인증 정보는 이미 저장소에 있다.
            InvalidateActiveContext();
            ApplyContext();
        }

        private void CloneAndConnect(string remoteUrl, string targetPath)
        {
            // Task 7에서 진행률·취소·실패 처리와 함께 배선한다.
            var path = _gitManager.CloneRepository(remoteUrl, targetPath, null, CancellationToken.None);
            AdoptRepository(path);
        }
```

- [x] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ConnectRepositoryCommand"`
Expected: 4개 PASS (기존 `CanExecute` 테스트 포함)

- [x] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/Services/IRepositoryConnectDialog.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs
git commit -m "feat(vsix): 저장소 연결을 폴더 선택과 원격 받기 두 갈래로 넓힌다"
```

---

### Task 7: 받는 동안 진행률과 취소를 화면에 붙인다

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`CloneAndConnect` 교체, `CloneProgressRelay` 추가)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitManager.CloneRepository`(Task 2~4), `CloneProgress`·`ClonePhase`(Task 2), `RepositoryConnectRequest.ForClone`·`AdoptRepository`(Task 6)
- Produces: 없음 (ViewModel 내부 배선)

- [x] **Step 1: 실패하는 테스트를 쓴다**

저장소 연결 절에 이어 쓴다. 파일 상단 `using` 에 `using DBVC.Core;`, `using DBVC.Core.Models;`, `using System.Threading;` 이 있는지 확인한다.

```csharp
        [Test]
        public void ConnectRepositoryCommand_ClonesAndSavesTheMapping_WhenTheUserChoosesToClone()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");
            _git.Setup(g => g.CloneRepository(
                    "git@host:org/db-schema.git", @"C:\repos\db-schema",
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(@"C:\repos\db-schema");
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(Server, Database, @"C:\repos\db-schema"), Times.Once);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNotSaveTheMapping_WhenCloneFails()
        {
            // 절반만 받아진 저장소가 매핑되면 이후 모든 동작이 조용히 이상해진다.
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");
            _git.Setup(g => g.CloneRepository(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Throws(new GitRemoteException("원격과 통신하지 못했습니다."));
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Not.Empty);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNotReportCancellationAsAnError_WhenTheUserCancelsTheClone()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");
            _git.Setup(g => g.CloneRepository(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Throws(new OperationCanceledException("원격 저장소 받기를 취소했습니다."));
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty,
                "사용자가 누른 취소를 오류 상자로 알리면 자기가 누른 것을 오류로 되읽습니다");
            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void ConnectRepositoryCommand_StopsOfferingCancel_WhenTheCloneReachesCheckout()
        {
            // libgit2의 checkout 콜백은 중단을 받지 않는다. 눌러도 안 멈추는 버튼을
            // 살려 두면 사용자가 도구가 굳었다고 읽는다.
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");

            ViewChangesViewModel? vm = null;
            var cancellableDuringCheckout = true;

            _git.Setup(g => g.CloneRepository(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Returns((string url, string path, IProgress<CloneProgress>? progress, CancellationToken token) =>
                {
                    progress?.Report(new CloneProgress(ClonePhase.Transferring, 10, 100));
                    progress?.Report(new CloneProgress(ClonePhase.CheckingOut, 1, 10));
                    cancellableDuringCheckout = vm!.CancelCommand.CanExecute(null);
                    return path;
                });

            vm = NewConnectedViewModel();
            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(cancellableDuringCheckout, Is.False);
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ConnectRepositoryCommand"`
Expected: FAIL — 임시 `CloneAndConnect`는 진행률도 취소도 예외 처리도 하지 않는다

- [x] **Step 3: 배선한다**

Task 6이 넣은 임시 `CloneAndConnect`를 아래로 교체한다.

```csharp
        /// <summary>
        /// 원격에서 받아 매핑까지 만든다. 저장소를 받는 동안 SSMS가 멈추면 안 되므로
        /// 새로고침과 같은 이음매로 UI 스레드 밖에 내보낸다.
        /// </summary>
        private void CloneAndConnect(string remoteUrl, string targetPath)
        {
            _extractionCancellation?.Dispose();
            _extractionCancellation = new CancellationTokenSource();
            var token = _extractionCancellation.Token;

            _cancellableWorkOutstanding = true;
            IsBusy = true;
            ProgressText = "원격 저장소를 받는 중...";
            RaiseActionCanExecuteChanged();

            // 보고는 백그라운드 스레드에서 온다. 바인딩 속성은 UI 스레드에서만 바꾼다.
            var progress = new CloneProgressRelay(p =>
            {
                var text = p.Phase == ClonePhase.Transferring
                    ? (p.Total > 0 ? $"받는 중... {p.Completed}/{p.Total} 객체" : "받는 중...")
                    : "펼치는 중...";

                // 펼치는 단계는 libgit2가 중단을 받지 않는다. 취소 버튼을 살려 두면
                // 눌러도 아무 일이 없고 "취소하는 중..."만 남는다.
                var stillCancellable = p.Phase == ClonePhase.Transferring;

                _scheduler.Post(() =>
                {
                    ProgressText = text;
                    if (_cancellableWorkOutstanding != stillCancellable)
                    {
                        _cancellableWorkOutstanding = stillCancellable;
                        RaiseActionCanExecuteChanged();
                    }
                });
            });

            _scheduler.Run(
                () => _gitManager.CloneRepository(remoteUrl, targetPath, progress, token),
                localPath =>
                {
                    _cancellableWorkOutstanding = false;
                    IsBusy = false;
                    ProgressText = null;
                    AdoptRepository(localPath);
                },
                ex =>
                {
                    _cancellableWorkOutstanding = false;
                    IsBusy = false;
                    ProgressText = null;
                    RaiseActionCanExecuteChanged();

                    // 취소는 실패가 아니다. 받다 만 폴더는 Core가 이미 지웠고 매핑도 만들지
                    // 않았으므로 사용자는 같은 경로로 다시 시도할 수 있다.
                    if (ex is OperationCanceledException)
                    {
                        WarningMessage = "원격 저장소 받기를 취소했습니다. 다시 시도할 수 있습니다.";
                        return;
                    }

                    _notifier.ShowError("DBVC 저장소 받기 실패", ex.Message);
                });
        }
```

`ExtractionProgressRelay`(약 1126행) 옆에 같은 모양의 릴레이를 더한다.

```csharp
        /// <summary>
        /// clone 보고를 그 자리에서 전달한다. 이유는 <see cref="ExtractionProgressRelay"/>와 같다.
        /// </summary>
        private sealed class CloneProgressRelay : IProgress<CloneProgress>
        {
            private readonly Action<CloneProgress> _onReport;
            public CloneProgressRelay(Action<CloneProgress> onReport) { _onReport = onReport; }
            public void Report(CloneProgress value) => _onReport(value);
        }
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ConnectRepositoryCommand"`
Expected: 8개 PASS

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 저장소를 받는 동안 진행률을 띄우고 전송 단계에서만 취소를 연다"
```

---

### Task 8: 원격 확인 버튼과 결과 표시

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (명령 배선 약 110행, 속성 추가, `InvalidateActiveContext` 약 202행, `RaiseActionCanExecuteChanged` 약 1398행)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (약 56행 브랜치 표시 옆, 약 152행 버튼 줄)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitManager.FetchRemoteStatus`·`RemoteStatus`(Task 5)
- Produces: `ViewChangesViewModel.CheckRemoteCommand`(`ICommand`), `ViewChangesViewModel.RemoteStatusText`(`string?`), `ViewChangesViewModel.HasRemoteStatus`(`bool`)

- [x] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        // ---------- 원격 확인 ----------

        [Test]
        public void CheckRemoteCommand_ShowsAheadAndBehindCounts_WhenTheRemoteAnswers()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 1));
            var vm = NewConnectedViewModel();

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(vm.RemoteStatusText, Does.Contain("받을 커밋 1개"));
            Assert.That(vm.RemoteStatusText, Does.Contain("올릴 커밋 2개"));
            Assert.That(vm.HasRemoteStatus, Is.True);
        }

        [Test]
        public void RemoteStatusText_IsEmpty_BeforeTheUserAsks()
        {
            // 누르기 전에는 아무것도 뜨지 않는다. 낡은 숫자를 최신인 척 보여주지 않기 위해서다.
            var vm = NewConnectedViewModel();

            Assert.That(vm.HasRemoteStatus, Is.False);
            _git.Verify(g => g.FetchRemoteStatus(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void RemoteStatusText_IsCleared_WhenTheTargetChanges()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 1));
            var vm = NewConnectedViewModel();
            vm.CheckRemoteCommand.Execute(null);

            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S2", "D2", SqlAuthMode.Windows, null, null, null));
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.HasRemoteStatus, Is.False,
                "다른 대상의 원격 상태가 남으면 사용자가 엉뚱한 저장소의 숫자를 읽습니다");
        }

        [Test]
        public void CheckRemoteCommand_ReportsTheReason_WhenTheRemoteCannotBeReached()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database))
                .Throws(new GitRemoteException("원격과 통신하지 못했습니다."));
            var vm = NewConnectedViewModel();

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Not.Empty);
            Assert.That(vm.HasRemoteStatus, Is.False);
        }

        [Test]
        public void CheckRemoteCommand_IsDisabled_WhenTheRepositoryIsBlocked()
        {
            // 차단은 경고가 아니다. 기준이 어긋난 저장소에서 낸 숫자는 조용히 거짓말이다.
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState
                {
                    CurrentBranch = "develop",
                    BlockReason = RepositoryBlockReason.BranchMismatch,
                    BlockMessage = "고정된 브랜치와 다릅니다."
                });
            var vm = NewConnectedViewModel();

            Assert.That(vm.CheckRemoteCommand.CanExecute(null), Is.False);
        }
```

> `RepositoryBlockReason.BranchMismatch` 는 1차가 만든 열거값이다. 이름이 다르면
> `src/DBVC.Core/Models/RepositoryState.cs` 에서 실제 값을 확인해 바꾼다.

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~CheckRemote|FullyQualifiedName~RemoteStatusText"`
Expected: 컴파일 실패 — `CheckRemoteCommand`가 없다

- [x] **Step 3: ViewModel에 더한다**

생성자의 명령 배선 목록(약 110행) 끝에 더한다.

```csharp
            CheckRemoteCommand = new RelayCommand(CheckRemote, CanCheckRemote);
```

`PushCommand` 속성 근처에 더한다.

```csharp
        public ICommand CheckRemoteCommand { get; }

        private string? _remoteStatusText;

        /// <summary>
        /// 마지막으로 원격을 확인한 결과. 누르기 전에는 <c>null</c>이다 —
        /// 낡은 숫자를 최신인 척 보여주는 것이 아무것도 안 보여주는 것보다 나쁘다.
        /// </summary>
        public string? RemoteStatusText
        {
            get => _remoteStatusText;
            private set
            {
                if (_remoteStatusText == value) return;
                _remoteStatusText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasRemoteStatus));
            }
        }

        public bool HasRemoteStatus => !string.IsNullOrWhiteSpace(RemoteStatusText);

        private bool CanCheckRemote() => IsMapped && !IsBusy && !IsBlocked;

        /// <summary>
        /// 원격을 받아 앞섬·뒤처짐을 센다. 수동 버튼으로만 돈다 — 새로고침에 붙이면
        /// 응답 없는 원격이 변경 목록을 보는 일까지 느리게 만든다.
        /// </summary>
        private void CheckRemote()
        {
            if (!CanCheckRemote()) return;

            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            ProgressText = "원격을 확인하는 중...";
            RaiseActionCanExecuteChanged();

            _scheduler.Run(
                () => _gitManager.FetchRemoteStatus(server, database),
                status =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    RemoteStatusText = $"받을 커밋 {status.BehindBy}개 · 올릴 커밋 {status.AheadBy}개";
                    RaiseActionCanExecuteChanged();
                },
                ex =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    RemoteStatusText = null;
                    RaiseActionCanExecuteChanged();
                    _notifier.ShowError("DBVC 원격 확인 실패", ex.Message);
                });
        }
```

`InvalidateActiveContext`(약 202행)의 `CurrentBranch = null;` 바로 아래에 더한다.

```csharp
            // 원격 상태도 이전 대상의 것이다. 남으면 엉뚱한 저장소의 숫자를 읽는다.
            RemoteStatusText = null;
```

`RaiseActionCanExecuteChanged`(약 1398행)에 더한다.

```csharp
            (CheckRemoteCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~CheckRemote|FullyQualifiedName~RemoteStatusText"`
Expected: 5개 PASS

- [x] **Step 5: 화면에 붙인다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml` 의 `BranchLabel`(약 56~61행) **바로 아래**에 더한다. DockPanel은 먼저 Dock된 것이 더 바깥이라, 브랜치 왼쪽에 놓으려면 뒤에 와야 한다.

```xml
                <TextBlock x:Name="RemoteStatusLabel" DockPanel.Dock="Right"
                           Text="{Binding RemoteStatusText}"
                           VerticalAlignment="Top" Margin="8,4,0,4"
                           Visibility="{Binding HasRemoteStatus, Converter={StaticResource BoolToVis}}"
                           Foreground="{DynamicResource {x:Static vsshell:VsBrushes.GrayTextKey}}"
                           ToolTip="마지막으로 '원격 확인'을 누른 시점의 값입니다. 자동으로 갱신되지 않습니다."/>
```

같은 파일의 `Push` 버튼(약 152행) 바로 아래에 더하고, Push 버튼의 `Margin="0,0,16,4"` 는 `Margin="0,0,10,4"` 로 바꾼다(간격이 두 번 벌어지지 않게).

```xml
                <Button Content="원격 확인" Command="{Binding CheckRemoteCommand}" Width="80" Margin="0,0,16,4"
                        ToolTip="원격을 받아 받을 커밋과 올릴 커밋의 수를 셉니다. 작업 트리는 건드리지 않습니다.&#10;누를 때만 네트워크를 씁니다 - 자동으로 갱신되지 않습니다."/>
```

- [x] **Step 6: 레이아웃 테스트가 깨지지 않았는지 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 전부 PASS. `TopRowLayoutTests`가 실패하면 새 `TextBlock`이 버전·브랜치 표시를 밀어낸 것이므로 Dock 순서를 다시 본다

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 원격 확인 버튼으로 받을 커밋과 올릴 커밋을 센다"
```

---

### Task 9: 두 갈래 대화상자를 WPF로 만든다

CI가 검증하지 않는 영역이다. 여기를 건드렸다면 SSMS 21에서 직접 눌러 보기 전에는 "동작한다"고 말할 수 없다.

**Files:**
- Create: `src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml`, `src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml.cs`
- Create: `src/DBVC.Vsix/Services/RepositoryConnectDialogAdapter.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:94` (기본 구현 복원)
- Modify: `src/DBVC.Vsix/DBVC.Vsix.csproj` (XAML이 자동 포함되지 않는 경우에만)

**Interfaces:**
- Consumes: `IRepositoryConnectDialog`·`RepositoryConnectRequest`(Task 6), `RemoteUrlNaming.SuggestFolderName`(Task 1), `IFolderBrowseDialog`(기존)
- Produces: `public sealed class DBVC.Vsix.Services.RepositoryConnectDialogAdapter : IRepositoryConnectDialog`

- [x] **Step 1: 대화상자를 만든다**

`src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml`:

```xml
<Window x:Class="DBVC.Vsix.UI.RepositoryConnectDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Git 저장소 연결" Width="560" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False">
    <StackPanel Margin="14">
        <TextBlock x:Name="TargetLabel" FontWeight="SemiBold" Margin="0,0,0,10" TextWrapping="Wrap"/>

        <RadioButton x:Name="ExistingChoice" GroupName="Kind" IsChecked="True"
                     Content="이미 받아둔 폴더를 연결합니다"/>
        <DockPanel Margin="20,6,0,12" IsEnabled="{Binding IsChecked, ElementName=ExistingChoice}">
            <Button DockPanel.Dock="Right" Content="찾아보기..." Width="90" Margin="6,0,0,0"
                    Click="BrowseExisting_Click"/>
            <TextBox x:Name="ExistingPathBox"/>
        </DockPanel>

        <RadioButton x:Name="CloneChoice" GroupName="Kind" Content="원격 저장소에서 새로 받습니다"/>
        <Grid Margin="20,6,0,0" IsEnabled="{Binding IsChecked, ElementName=CloneChoice}">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Grid.Column="0" Text="원격 주소" VerticalAlignment="Center" Margin="0,0,8,6"/>
            <TextBox x:Name="RemoteUrlBox" Grid.Row="0" Grid.Column="1" Grid.ColumnSpan="2" Margin="0,0,0,6"
                     TextChanged="RemoteUrl_TextChanged"/>

            <TextBlock Grid.Row="1" Grid.Column="0" Text="받을 위치" VerticalAlignment="Center" Margin="0,0,8,6"/>
            <TextBox x:Name="ParentFolderBox" Grid.Row="1" Grid.Column="1" Margin="0,0,6,6"/>
            <Button Grid.Row="1" Grid.Column="2" Content="찾아보기..." Width="90" Margin="0,0,0,6"
                    Click="BrowseParent_Click"/>

            <TextBlock Grid.Row="2" Grid.Column="0" Text="폴더 이름" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBox x:Name="FolderNameBox" Grid.Row="2" Grid.Column="1" Grid.ColumnSpan="2"
                     TextChanged="FolderName_TextChanged"/>
        </Grid>

        <!-- SSH만 지원한다는 사실을 여기서 한 번 말해 두면 실패한 뒤에 배우지 않아도 된다. -->
        <TextBlock Margin="20,8,0,0" TextWrapping="Wrap" Opacity="0.8"
                   Text="원격 주소는 SSH 형식이어야 합니다(예: git@호스트:그룹/이름.git). HTTPS 주소는 인증할 수 없습니다. 폴더 이름은 아직 없는 것이어야 합니다."/>

        <TextBlock x:Name="ErrorLabel" Margin="0,10,0,0" Foreground="#B00020"
                   TextWrapping="Wrap" Visibility="Collapsed"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,14,0,0">
            <Button Content="확인" Width="90" Margin="0,0,8,0" IsDefault="True" Click="Ok_Click"/>
            <Button Content="취소" Width="90" IsCancel="True"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml.cs`:

```csharp
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DBVC.Core;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 저장소 연결 방식을 묻는다. 원격 주소가 쓸 수 있는 것인지는 판정하지 않는다 —
    /// GitManager가 네트워크를 타기 전에 거른다. 여기서도 판정하면 같은 규칙이 두 곳에 생기고
    /// 언젠가 갈라진다.
    /// </summary>
    public partial class RepositoryConnectDialog : Window
    {
        private readonly IFolderBrowseDialog _folderDialog;

        /// <summary>사용자가 폴더 이름을 직접 고쳤는지. 고쳤으면 제안이 덮어쓰지 않는다.</summary>
        private bool _folderNameEditedByUser;

        public RepositoryConnectDialog(string serverName, string databaseName, IFolderBrowseDialog folderDialog)
        {
            InitializeComponent();
            _folderDialog = folderDialog;
            TargetLabel.Text = $"'{serverName}.{databaseName}'의 스크립트를 보관할 Git 저장소를 지정하세요.";
        }

        public RepositoryConnectRequest? Result { get; private set; }

        private void BrowseExisting_Click(object sender, RoutedEventArgs e)
        {
            var path = _folderDialog.PromptForFolder("이미 받아둔 Git 저장소 폴더를 선택하세요.", ExistingPathBox.Text);
            if (path != null) ExistingPathBox.Text = path;
        }

        private void BrowseParent_Click(object sender, RoutedEventArgs e)
        {
            var path = _folderDialog.PromptForFolder("저장소를 받을 상위 폴더를 선택하세요.", ParentFolderBox.Text);
            if (path != null) ParentFolderBox.Text = path;
        }

        private void RemoteUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (FolderNameBox == null || _folderNameEditedByUser) return;

            var suggested = RemoteUrlNaming.SuggestFolderName(RemoteUrlBox.Text) ?? string.Empty;

            // 제안이 만든 변경은 사용자가 고친 것으로 세면 안 된다.
            _folderNameEditedByUser = true;
            FolderNameBox.Text = suggested;
            _folderNameEditedByUser = false;
        }

        private void FolderName_TextChanged(object sender, TextChangedEventArgs e)
        {
            _folderNameEditedByUser = true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (ExistingChoice.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ExistingPathBox.Text))
                {
                    ShowError("연결할 폴더를 선택하세요.");
                    return;
                }

                Result = RepositoryConnectRequest.ForExistingFolder(ExistingPathBox.Text.Trim());
                DialogResult = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(RemoteUrlBox.Text))
            {
                ShowError("원격 주소를 입력하세요.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ParentFolderBox.Text) || string.IsNullOrWhiteSpace(FolderNameBox.Text))
            {
                ShowError("받을 위치와 폴더 이름을 모두 지정하세요.");
                return;
            }

            var target = Path.Combine(ParentFolderBox.Text.Trim(), FolderNameBox.Text.Trim());
            Result = RepositoryConnectRequest.ForClone(RemoteUrlBox.Text.Trim(), target);
            DialogResult = true;
        }

        private void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.Visibility = Visibility.Visible;
        }
    }
}
```

- [x] **Step 2: 어댑터를 만든다**

`src/DBVC.Vsix/Services/RepositoryConnectDialogAdapter.cs`:

```csharp
using DBVC.Vsix.UI;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 실제 WPF 대화상자를 띄우는 구현. 폴더 선택은 기존 어댑터에 위임한다 —
    /// net48 WPF에는 폴더 선택 대화상자가 없어 Windows Forms의 것을 쓰는 사정이 그대로 남는다.
    /// </summary>
    public sealed class RepositoryConnectDialogAdapter : IRepositoryConnectDialog
    {
        private readonly IFolderBrowseDialog _folderDialog;

        public RepositoryConnectDialogAdapter(IFolderBrowseDialog? folderDialog = null)
        {
            _folderDialog = folderDialog ?? new FolderBrowserDialogAdapter();
        }

        public RepositoryConnectRequest? Prompt(string serverName, string databaseName)
        {
            var dialog = new RepositoryConnectDialog(serverName, databaseName, _folderDialog)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            return dialog.ShowDialog() == true ? dialog.Result : null;
        }
    }
}
```

- [x] **Step 3: ViewModel의 기본 구현을 되돌린다**

Task 6 Step 4에서 임시로 넣은 줄을 아래로 바꾸고, **`NoOpRepositoryConnectDialog` 중첩 클래스를 지운다.**

```csharp
            _connectDialog = connectDialog ?? new RepositoryConnectDialogAdapter();
```

- [x] **Step 4: 빌드를 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공. XAML이 빌드에 포함되지 않는다는 오류가 나면 `DBVC.Vsix.csproj`에서 기존 `ViewChangesControl.xaml`이 어떻게 포함되는지 확인하고 같은 방식으로 맞춘다

- [x] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 전부 PASS 또는 Skip

- [x] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml src/DBVC.Vsix/UI/RepositoryConnectDialog.xaml.cs src/DBVC.Vsix/Services/RepositoryConnectDialogAdapter.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/DBVC.Vsix.csproj
git commit -m "feat(vsix): 저장소 연결 대화상자에서 원격 주소와 받을 위치를 받는다"
```

---

### Task 10: 문서와 버전을 맞춘다

**Files:**
- Modify: `README.md`
- Modify: `docs/setup-checklist.md` (3단계, 6단계, 7단계 검증 목록)
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest:4`

**Interfaces:**
- Consumes: Task 1~9 전부
- Produces: 없음

- [x] **Step 1: 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest` 의 `Identity` 에서 `Version="0.3.2"` 를 `Version="0.4.0"` 으로 바꾼다.

> 버전의 출처는 이 파일 하나다. `DbvcVersion`은 빌드 시 csproj가 흘려 넣는 값을 읽으므로
> 코드에 숫자를 적지 않는다.

- [x] **Step 2: README를 고친다**

`### 기능 커버리지` 목록의 Git Pull·Push 항목 옆에 더한다.

```markdown
- **저장소 받기:** **저장소 연결...** 에서 이미 받아둔 폴더를 고르거나, 원격 주소를 넣어 **그 자리에서 받을 수 있습니다.** 받는 동안 진행률이 뜨고, 받는 단계에서는 취소할 수 있습니다(펼치는 단계는 libgit2가 중단을 받지 않아 취소 버튼이 잠깁니다). 실패하거나 취소하면 받다 만 폴더를 지우고 매핑도 만들지 않으므로 같은 경로로 다시 시도할 수 있습니다. 받을 폴더는 **아직 없는 것이어야 합니다** — 그래야 지워도 되는 것과 안 되는 것을 구분할 필요가 없습니다.
- **원격 확인:** **원격 확인** 은 원격을 받아 `받을 커밋 n개 · 올릴 커밋 n개` 를 상단에 띄웁니다. 참조만 갱신하고 작업 트리는 건드리지 않습니다. **누를 때만 네트워크를 쓰며 자동으로 갱신되지 않습니다** — 새로고침에 붙이면 응답 없는 원격이 변경 목록을 보는 일까지 느리게 만들기 때문입니다.
```

`**Git 인증은 SSH만 지원합니다.**` 문단(약 109행) 끝에 더한다.

```markdown
저장소를 받을 때도 같습니다 — HTTPS 주소를 넣으면 네트워크를 타기 전에 거부하고 SSH로 바꾸는 방법을 안내합니다. **SSH 키 생성과 `known_hosts` 등록은 여전히 터미널에서 한 번 해야 합니다**(`ssh-keygen`, `ssh -T`). libgit2가 위임하는 `ssh.exe`는 SSMS 안에서 호스트 키 확인을 물을 수 없기 때문입니다.
```

- [x] **Step 3: 도입 체크리스트를 고친다**

`docs/setup-checklist.md` 3단계의 PowerShell `git clone` 항목을 아래로 바꾼다. **2단계(SSH 준비)는 그대로 둔다.**

```markdown
- [x] **DBVC에서 받는다.** 4단계에서 SSMS에 설치한 뒤, 도구 창의 **저장소 연결...** 에서
      **원격 저장소에서 새로 받습니다** 를 고르고 SSH URL과 받을 위치를 넣는다.
      (터미널에서 `git clone` 을 해도 되며, 그 경우 **이미 받아둔 폴더를 연결합니다** 를 쓴다.)

  > SSH URL은 `git@github.com:...` 형태다. `https://github.com/...` 을 넣으면 DBVC가
  > 받기 전에 거부하면서 SSH로 바꾸는 방법을 안내한다.

  > **받을 위치.** OneDrive가 동기화하는 폴더(바탕 화면·문서)는 피한다. Windows 11에서는
  > 이 폴더들이 기본으로 OneDrive 백업 대상이라 `.git` 내부 파일이 동기화와 충돌할 수 있다.

  > **폴더 이름은 아직 없는 것이어야 한다.** DBVC는 이미 있는 폴더에 받지 않는다 —
  > 그래야 실패하거나 취소했을 때 받다 만 것만 정확히 지울 수 있다.

  > **2단계의 `ssh -T` 를 건너뛰면 여기서 실패한다.** DBVC가 위임하는 `ssh.exe` 는
  > SSMS 안에서 호스트 키 확인을 물을 수 없어 `known_hosts` 에 없는 호스트에서 그냥 멈춘다.
```

6단계의 `git clone` 항목에도 같은 안내를 넣는다. **`ssh -T git@<gitlab-호스트>` 로 `known_hosts` 를 등록하는 항목은 그대로 둔다.**

7단계에 절을 더한다.

```markdown
### 0.4.0 — 저장소 받기와 원격 확인

- [x] **저장소 연결...** 에 두 갈래가 보이고, 원격 주소를 넣으면 폴더 이름이 자동으로 채워진다
- [x] 폴더 이름을 손으로 고치면 그 뒤로 제안이 덮어쓰지 않는다
- [x] 받는 동안 진행률이 올라가고, **받는 중** 에는 취소 버튼이 눌린다
- [x] 취소하면 받다 만 폴더가 사라지고, 같은 경로로 다시 받을 수 있다
- [x] HTTPS 주소를 넣으면 즉시 거부하며 SSH로 바꾸는 방법을 안내한다
- [x] `known_hosts` 에 없는 호스트에서는 실패하고, 안내에 `ssh -T` 가 있다
- [x] **원격 확인** 의 숫자가 `git -C <폴더> status -sb` 와 맞는다
- [x] 대상 데이터베이스를 바꾸면 원격 확인 결과가 사라진다
```

- [x] **Step 4: 전체 빌드와 테스트를 확인한다**

Run: `dotnet build DBVC.slnx`
Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 전부 PASS 또는 Skip

- [x] **Step 5: `.vsix` 산출물을 확인한다**

Run: `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release`
Run: `dir src\DBVC.Vsix\bin\Release\net48\*.vsix`
Expected: `.vsix` 파일이 존재한다. **빌드 성공 ≠ `.vsix` 생성이다** — 없으면 개발자 셸에서 `msbuild src/DBVC.Vsix/DBVC.Vsix.csproj -restore -p:Configuration=Release` 로 한 번 더 확인한다

- [x] **Step 6: 커밋**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest
git commit -m "docs: 저장소 받기와 원격 확인을 문서에 반영하고 0.4.0으로 올린다"
```

---

## 완료 조건

CI가 검증하지 않는 영역이 있다. **아래를 실제로 눌러 보기 전에는 "동작한다"고 말할 수 없다.**

- [x] **SSMS 21에 `.vsix`를 설치하고 도구 창의 버전이 `0.4.0`이다.** 덮어 설치 후 SSMS를 다시 시작해야 반영된다
- [x] **매핑이 없는 데이터베이스에서 "저장소 연결..."을 누르면 두 갈래 대화상자가 뜬다**
- [x] **SSH URL을 붙여 넣으면 폴더 이름이 자동으로 채워지고, 손으로 고치면 그 뒤로 덮어쓰지 않는다**
- [x] **실제 원격(GitHub 또는 사내 GitLab)에서 받아지고, 받은 폴더가 그대로 매핑된다.** 매핑 경로가 `.git`이 아니라 작업 트리다
- [x] **받은 직후 Push가 "추적 중인 원격 브랜치가 없어" 로 거부되지 않는다** — clone이 upstream을 만든다는 근거다
- [x] **큰 저장소에서 전송 진행률이 실제로 올라간다.** 파일 경로 원격을 쓰는 단위 테스트로는 이 경로가 검증되지 않는다
- [x] **전송 중 취소 버튼이 눌리고, 누르면 폴더가 사라진다**
- [x] **펼치는 단계로 넘어가면 취소 버튼이 잠긴다**
- [x] **HTTPS URL을 넣으면 즉시 거부하고 SSH 안내가 뜬다.** 폴더는 만들어지지 않는다
- [x] **`known_hosts`에 없는 호스트로 받으면 실패하고, 안내에 `ssh -T` 가 있다**
- [x] **`ssh.exe`를 `core.sshCommand`로만 가리키는 PC(Git for Windows만 설치)에서, 받기가 실패했을 때 "OpenSSH 클라이언트를 설치하세요"가 아니라 공개키·`known_hosts`·포트 확인 목록이 나온다** — `IsSshAvailableWithoutRepository`가 전역 config를 읽는다는 근거다
- [x] **이미 있는 폴더 이름을 넣으면 받기 전에 거부한다**
- [x] **받는 동안 SSMS가 잠기지 않는다** — 쿼리 편집기와 개체 탐색기가 그대로 동작한다
- [x] **"원격 확인"의 숫자가 같은 폴더에서 `git status -sb` 로 본 것과 같다**
- [x] **차단 오버레이가 뜬 저장소에서는 "원격 확인"이 눌리지 않는다**
- [x] **대상 데이터베이스를 바꾸면 원격 확인 결과가 사라진다**

## 남는 것

이 계획이 끝나도 **터미널이 완전히 사라지지는 않는다.** `ssh-keygen`과 `ssh -T`는 그대로 남는다 —
libgit2가 위임하는 `ssh.exe`가 SSMS 안에서 호스트 키 확인을 물을 수 없고, `known_hosts` 자동
등록은 중간자 공격에 문을 여는 일이라 SSH 우선 설계가 거부했다. 2차가 없애는 것은 `git clone`
한 줄이다.

3차(배포와 감사)는 이 계획과 독립적이다. spec §7.3을 따른다.
