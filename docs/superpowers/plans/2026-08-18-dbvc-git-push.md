# DBVC Git Push 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** View Changes 도구 창의 Push 버튼 하나로 로컬 커밋을 원격 저장소에 올린다.

**Architecture:** `LibGit2Sharp.Network.Push` 를 `GitManager.PushChanges` 로 감싼다. SSH 인증은 libgit2가 시스템 `ssh` 에 위임하므로 Pull과 같은 경로이며, 기존 `RemoteDiagnostics`·`SshExecutableLocator`·`GitAuthenticationException`·`GitRemoteException` 을 그대로 재사용한다. Pull과 중복되는 원격·추적 검증은 private 헬퍼로 먼저 추출한 뒤 둘이 공유한다.

**Tech Stack:** C# / .NET Standard 2.0 + .NET Framework 4.8, LibGit2Sharp 0.32.0, WPF(MVVM), NUnit 4, Moq.

**설계 문서:** [docs/superpowers/specs/2026-08-18-dbvc-git-push-design.md](../specs/2026-08-18-dbvc-git-push-design.md)

## Global Constraints

- **패키지 버전을 올리지 말 것.** `LibGit2Sharp 0.32.0`, `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0` 은 SSMS 21이 프로세스에 먼저 올리는 어셈블리에 맞춰 고정돼 있다. `DBVC.Core.csproj` 의 주석 참조. 이 계획은 새 패키지를 필요로 하지 않는다.
- **사용자에게 보이는 모든 문구는 한국어.** 예외 메시지, 알림, ToolTip 포함. libgit2의 영문 원문은 서버 응답을 인용할 때만 그대로 싣는다.
- **libgit2/서버 메시지를 문자열로 매칭해 분기하지 않는다.** 버전·전송 방식에 따라 달라진다. 타입과 콜백 호출 여부만 근거로 쓴다.
- **주석은 "왜"만 적는다.** 이 저장소의 기존 주석 밀도와 문체(한국어 평서문, 함정과 근거를 남김)를 따른다.
- **TDD.** 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋. 각 태스크가 이 순환을 한 번 이상 돈다.
- **커밋 메시지는 한국어 명령형 현재시제.** 기존 이력 형태: `feat(core): 메모리 전용 자격증명 저장소를 더한다`.
- **테스트 실행 명령**
  ```bash
  dotnet test tests/DBVC.Core.Tests
  dotnet test tests/DBVC.Vsix.Tests
  ```
  단일 테스트: `dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~테스트이름"`
- **`.vsct`/`.vsix` 패키징은 이 계획의 범위 밖이다.** 새 메뉴 명령을 추가하지 않고 기존 도구 창 안에 버튼만 더하므로 `.vsct` 를 건드리지 않는다.

---

## File Structure

**신규**

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/Models/PushResult.cs` | Push 결과 열거형 |
| `src/DBVC.Core/GitPushRejectedException.cs` | 원격이 ref 갱신을 거부했음을 알리는 예외 하나 |

**수정**

| 파일 | 변경 |
| --- | --- |
| `src/DBVC.Core/Abstractions.cs` | `IGitManager.PushChanges` 선언 |
| `src/DBVC.Core/GitManager.cs` | 원격 검증 헬퍼 추출, `BuildPushOptions`, `PushChanges` |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | `PushCommand` 와 `Push()` |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | Push 버튼 |
| `tests/DBVC.Core.Tests/GitManagerTests.cs` | Push 테스트와 bare 원격 헬퍼 |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | `PushCommand` 테스트 |
| `README.md`, `docs/setup-checklist.md`, `docs/superpowers/specs/2026-08-03-dbvc-ssh-first-git-auth-design.md` | "Push 없음" 서술 정정 |

`PushResult` 는 `Models/` 아래 **자체 파일**로 만든다. `ScriptTargetInfo.cs` 가 두 타입을 한 파일에 담고 있긴 하지만 그 둘은 서로를 참조하는 짝이다. `PushResult` 는 그 어느 쪽과도 무관하다.

---

## Task 1: `PullChanges` 의 원격 검증부를 헬퍼로 추출한다

Push가 그대로 복제할 코드를 먼저 한 곳으로 모은다. **동작을 바꾸지 않는 순수 리팩터링이다.** 기존 Pull 테스트가 전부 그대로 통과하는 것이 이 태스크의 합격 기준이다.

**Files:**
- Modify: `src/DBVC.Core/GitManager.cs` (`PullChanges`, 약 192-232행)

**Interfaces:**
- Consumes: 없음
- Produces: `private static string? ValidateRemoteAndBuildGuidance(Repository repo, string repoPath, string operationName)` — 원격 미설정/추적 브랜치 없음이면 `InvalidOperationException` 을 던지고, 아니면 `RemoteDiagnostics.Explain` 의 결과(안내가 없으면 `null`)를 돌려준다. `operationName` 은 `"Pull"` 또는 `"Push"` 로 메시지에 그대로 박힌다.

- [x] **Step 1: 기존 Pull 테스트가 지금 통과하는지 먼저 확인한다**

리팩터링의 기준선을 잡는 단계다. 여기서 실패하는 것이 있으면 리팩터링을 시작하지 말고 보고한다.

Run: `dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~PullChanges"`
Expected: PASS (`[Explicit]` 표시된 것은 실행되지 않는다)

- [x] **Step 2: 헬퍼를 추가한다**

`GitManager.cs` 의 `BuildPullOptions` 바로 위에 넣는다.

```csharp
        /// <summary>
        /// 원격 연산의 공통 선행 조건을 검사하고, 실패했을 때 덧붙일 안내를 미리 계산한다.
        /// Pull과 Push가 글자 그대로 같은 검사를 하므로 한 곳에 둔다 — 복제해 두면
        /// 한쪽 문구만 고쳐지는 일이 실제로 일어난다.
        /// </summary>
        /// <param name="operationName">메시지에 박히는 연산 이름. "Pull" 또는 "Push".</param>
        /// <returns>안내할 것이 없으면 <c>null</c>. 호출자는 <c>null</c>이면 원본 예외를 그대로 둔다.</returns>
        private static string? ValidateRemoteAndBuildGuidance(Repository repo, string repoPath, string operationName)
        {
            if (!repo.Network.Remotes.Any())
            {
                throw new InvalidOperationException($"'{repoPath}' 저장소에 원격(remote)이 설정되어 있지 않아 {operationName}할 수 없습니다.");
            }

            // 원격만 있고 추적 브랜치가 없으면 libgit2가 영문 원문으로 거부한다. DBVC 온보딩이 실제로
            // 만들어내는 상태다 - 사용자가 clone하지 않고 직접 git init한 폴더를 매핑하면 여기 걸린다.
            // 추적을 대신 설정해 주지는 않는다. 버튼 하나가 사용자의 git config를 조용히 바꾸면 안 된다.
            if (!repo.Head.IsTracking)
            {
                var branchName = repo.Head.FriendlyName;
                throw new InvalidOperationException(
                    $"'{repoPath}' 저장소의 현재 브랜치 '{branchName}'에 추적 중인 원격 브랜치가 없어 {operationName}할 수 없습니다. " +
                    $"Git 클라이언트에서 'git push -u origin {branchName}'을 한 번 실행해 추적을 설정한 뒤 다시 시도하세요.");
            }

            // Explain은 예외가 아니라 원격 URL과 ssh 실행 파일 유무만 보므로 통신 이전에 한 번 계산한다.
            // IsTracking이 true라도 RemoteName은 비어 있을 수 있다 - 브랜치가 로컬 브랜치를 추적하는 경우
            // (branch.<name>.remote = "." - `git branch --track`, autoSetupMerge = always 등으로 만들어진다)
            // RemoteName은 ""이고 Remotes[""]는 ArgumentNullException을 던진다. remoteUrl을 null로 두면
            // Explain이 Unknown으로 처리해 guidance가 null이 되고, 호출자의 catch가 가로채지 않아
            // libgit2의 원본 예외가 그대로 전파된다.
            var remoteName = repo.Head.RemoteName;
            var remoteUrl = string.IsNullOrEmpty(remoteName) ? null : repo.Network.Remotes[remoteName]?.Url;

            // SshExecutableLocator만으로는 부족하다 - libgit2의 ssh_exec 전송은 GIT_SSH(_COMMAND) 외에
            // core.sshCommand 설정값도 읽는다. OpenSSH 선택적 기능이 꺼져 있어도 Git for Windows의
            // ssh.exe를 core.sshCommand로 가리키는 구성(사내 PC에서 흔함)은 실제로 SSH가 되므로 여기서 함께 본다.
            var sshAvailable = SshExecutableLocator.IsAvailable()
                || !string.IsNullOrWhiteSpace(repo.Config.Get<string>("core.sshCommand")?.Value);

            return RemoteDiagnostics.Explain(remoteUrl, sshAvailable);
        }
```

- [x] **Step 3: `PullChanges` 가 헬퍼를 쓰게 바꾼다**

`PullChanges` 안에서 `if (!repo.Network.Remotes.Any())` 부터 `var guidance = RemoteDiagnostics.Explain(remoteUrl, sshAvailable);` 까지(주석 포함 전부)를 지우고 한 줄로 바꾼다.

```csharp
            var guidance = ValidateRemoteAndBuildGuidance(repo, repoPath, "Pull");
```

`var headBefore = repo.Head.Tip;` 이하는 건드리지 않는다.

- [x] **Step 4: 테스트가 그대로 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: PASS. 실패가 하나라도 있으면 문구가 바뀐 것이다 — 헬퍼의 문자열을 원본과 글자 단위로 대조한다.

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Core/GitManager.cs
git commit -m "refactor(core): Pull의 원격 검증부를 Push와 나눠 쓸 헬퍼로 뽑는다"
```

---

## Task 2: `PushResult`·`GitPushRejectedException`·`BuildPushOptions` 를 더한다

`PushChanges` 가 쓸 재료를 먼저 만든다. `BuildPushOptions` 는 `BuildPullOptions` 와 같은 이유로 `internal` 이다 — 자격 증명 콜백과 거부 수집 콜백이 **실제로 옵션에 연결됐는지**는 `PushChanges` 를 통째로 돌리지 않고는 검증할 수 없기 때문이다.

**Files:**
- Create: `src/DBVC.Core/Models/PushResult.cs`
- Create: `src/DBVC.Core/GitPushRejectedException.cs`
- Modify: `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `GitManager.ResolveCredentials(SupportedCredentialTypes, out bool)` (기존)
- Produces:
  - `enum DBVC.Core.Models.PushResult { NoMapping, NothingToPush, Pushed }`
  - `class DBVC.Core.GitPushRejectedException : Exception` — `(string message)`, `(string message, Exception innerException)`
  - `internal static PushOptions GitManager.BuildPushOptions(Action onUserCredentialsRequired, Action<PushStatusError> onPushStatusError)`

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs` 의 `// ---------- BuildPullOptions (자격 증명 배선) ----------` 섹션 **아래**에 새 섹션으로 넣는다.

```csharp
        // ---------- BuildPushOptions (콜백 배선) ----------

        [Test]
        public void BuildPushOptions_WiresResolveCredentialsIntoTheCredentialsProvider()
        {
            // Pull과 같은 이유다. ResolveCredentials가 단위 테스트를 통과하는 것과, 그것이 실제로
            // PushChanges가 쓰는 PushOptions에 연결되어 있는 것은 별개다. 파일 경로 원격을 쓰는
            // 다른 Push 테스트는 자격 증명 콜백을 아예 거치지 않으므로 이 배선을 지키지 못한다.
            var options = GitManager.BuildPushOptions(() => { }, _ => { });

            Assert.That(options.CredentialsProvider, Is.Not.Null,
                "CredentialsProvider가 비어 있으면 인증이 필요한 원격에서 항상 실패합니다");
        }

        [Test]
        public void BuildPushOptions_InvokesTheCredentialsCallback_OnlyWhenTheRemoteRequiresUserCredentials()
        {
            var requiresUserCredentialsCallCount = 0;
            var options = GitManager.BuildPushOptions(() => requiresUserCredentialsCallCount++, _ => { });

            // Default를 지원하는 원격: 통합 인증으로 처리되므로 콜백이 불리면 안 된다.
            options.CredentialsProvider!("https://example.com/repo.git", null, SupportedCredentialTypes.Default);
            Assert.That(requiresUserCredentialsCallCount, Is.Zero);

            // Default를 지원하지 않는 원격: 콜백이 불려야 PushChanges가 GitAuthenticationException으로 감쌀 수 있다.
            options.CredentialsProvider!("https://example.com/repo.git", null, SupportedCredentialTypes.UsernamePassword);
            Assert.That(requiresUserCredentialsCallCount, Is.EqualTo(1));
        }

        [Test]
        public void BuildPushOptions_WiresOnPushStatusError()
        {
            // 이 배선이 없으면 서버가 ref 갱신을 거부해도 Network.Push가 정상 반환한다.
            // 즉 실패가 성공으로 보고된다. 단위 테스트가 닿는 유일한 지점이므로 여기서 지킨다.
            var collected = 0;
            var options = GitManager.BuildPushOptions(() => { }, _ => collected++);

            Assert.That(options.OnPushStatusError, Is.Not.Null);
            options.OnPushStatusError!(new PushStatusError());
            Assert.That(collected, Is.EqualTo(1));
        }

        [Test]
        public void GitPushRejectedException_CarriesTheInnerException()
        {
            var inner = new InvalidOperationException("원본");
            var ex = new GitPushRejectedException("거부", inner);

            Assert.That(ex.Message, Is.EqualTo("거부"));
            Assert.That(ex.InnerException, Is.SameAs(inner));
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~BuildPushOptions|FullyQualifiedName~GitPushRejectedException"`
Expected: 컴파일 실패 — `BuildPushOptions` 와 `GitPushRejectedException` 이 없다.

- [x] **Step 3: `PushResult` 를 만든다**

`src/DBVC.Core/Models/PushResult.cs`

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// Push의 결과. <c>bool</c>이 아닌 이유는 "올릴 커밋이 없다"가 실패가 아니기 때문이다 —
    /// 매핑 실패와 한 값으로 묶으면 호출자가 정상 상태를 오류로 보고하게 된다.
    /// </summary>
    public enum PushResult
    {
        /// <summary>이 (서버, 데이터베이스)에 매핑된 저장소가 없다.</summary>
        NoMapping,

        /// <summary>원격이 이미 최신이다. 정상 상태이며 오류가 아니다.</summary>
        NothingToPush,

        /// <summary>커밋을 원격에 올렸다.</summary>
        Pushed
    }
}
```

- [x] **Step 4: `GitPushRejectedException` 을 만든다**

`src/DBVC.Core/GitPushRejectedException.cs`

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격이 ref 갱신을 거부해 Push가 이루어지지 않았음을 알린다. (설계 4.3)
    ///
    /// 거부는 두 경로로 온다 - libgit2가 스스로 판정하는 <c>NonFastForwardException</c>과
    /// 서버가 상태로 보고하는 <c>OnPushStatusError</c>다. 사용자에게는 같은 일이므로
    /// 한 타입으로 수렴시킨다.
    /// </summary>
    public class GitPushRejectedException : Exception
    {
        public GitPushRejectedException(string message) : base(message)
        {
        }

        public GitPushRejectedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
```

- [x] **Step 5: `BuildPushOptions` 를 더한다**

`GitManager.cs` 의 `BuildPullOptions` 바로 아래에 넣는다.

```csharp
        /// <summary>
        /// Push에 사용할 <see cref="PushOptions"/>를 만든다.
        /// <c>internal</c>로 노출하는 이유는 <see cref="BuildPullOptions"/>와 같다 —
        /// 파일 경로 원격을 쓰는 단위 테스트는 자격 증명 콜백도, 서버의 상태 보고도 거치지 않으므로
        /// 이 배선이 실제로 붙어 있는지는 여기서만 검증할 수 있다.
        /// </summary>
        /// <param name="onPushStatusError">
        /// 서버가 ref 갱신을 거부했을 때 호출된다. 이것을 연결하지 않으면
        /// <c>Network.Push</c>가 정상 반환해 실패가 성공으로 보고된다.
        /// </param>
        internal static PushOptions BuildPushOptions(
            Action onUserCredentialsRequired,
            Action<PushStatusError> onPushStatusError)
        {
            return new PushOptions
            {
                CredentialsProvider = (url, usernameFromUrl, types) =>
                {
                    var credentials = ResolveCredentials(types, out var needsUserCredentials);
                    if (needsUserCredentials) onUserCredentialsRequired();
                    return credentials;
                },
                OnPushStatusError = error => onPushStatusError(error)
            };
        }
```

- [x] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~BuildPushOptions|FullyQualifiedName~GitPushRejectedException"`
Expected: PASS (4개)

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Core/Models/PushResult.cs src/DBVC.Core/GitPushRejectedException.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): Push가 쓸 결과 타입·거부 예외·옵션 빌더를 더한다"
```

---

## Task 3: `GitManager.PushChanges` 를 구현한다

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs` (`IGitManager`)
- Modify: `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `ValidateRemoteAndBuildGuidance`(Task 1), `PushResult`·`GitPushRejectedException`·`BuildPushOptions`(Task 2)
- Produces: `PushResult IGitManager.PushChanges(string serverName, string databaseName)`

- [x] **Step 1: 테스트 헬퍼를 더한다**

`GitManagerTests.cs` 의 `NewRepoWithCommit` 바로 아래에 넣는다. **원격은 bare여야 한다** — 체크아웃된 브랜치를 가진 저장소로는 push가 거부된다.

```csharp
        /// <summary>
        /// bare 원격과 그것을 clone한 로컬 저장소를 만든다.
        /// 원격이 bare가 아니면 "체크아웃된 브랜치는 갱신할 수 없다"로 push가 거부되어,
        /// 우리가 검증하려는 거부 경로와 구분되지 않는다.
        /// </summary>
        private (string LocalPath, string OriginPath) NewClonedRepoWithBareOrigin()
        {
            var seedPath = NewRepoWithCommit();
            var originPath = NewTempDir();
            Repository.Clone(seedPath, originPath, new CloneOptions { IsBare = true });

            var localPath = NewTempDir();
            Repository.Clone(originPath, localPath);
            return (localPath, originPath);
        }

        /// <summary>해당 작업 트리에 파일 하나를 더하고 커밋한다. 커밋 SHA를 준다.</summary>
        private static string CommitOneFile(string repoPath, string relativePath, string content, string message)
        {
            WriteRepoFile(repoPath, relativePath, content);
            using var repo = new Repository(repoPath);
            Commands.Stage(repo, "*");
            return repo.Commit(message, TestSignature, TestSignature).Sha;
        }
```

- [x] **Step 2: 실패하는 테스트를 쓴다**

`// ---------- BuildPushOptions (콜백 배선) ----------` 섹션 **위**에, `PullChanges` 섹션 다음에 새 섹션으로 넣는다.

```csharp
        // ---------- PushChanges ----------

        [Test]
        public void PushChanges_ReturnsNoMapping_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var git = new GitManager(new ConfigManager(configPath));

            Assert.That(git.PushChanges("localhost", "testdb"), Is.EqualTo(PushResult.NoMapping));
        }

        [Test]
        public void PushChanges_ExplainsInKorean_WhenTheRepositoryHasNoRemote()
        {
            var localPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("원격"));
            Assert.That(ex.Message, Does.Contain("Push할 수 없습니다"),
                "어떤 연산이 막혔는지 이름으로 말해야 합니다");
        }

        [Test]
        public void PushChanges_ExplainsInKorean_WhenTheCurrentBranchHasNoUpstream()
        {
            // git init한 폴더를 매핑하면 실제로 나오는 상태다. 추적을 대신 설정하지 않고 안내만 한다.
            var originPath = NewRepoWithCommit();
            var localPath = NewRepoWithCommit();

            // 기본 브랜치 이름을 하드코딩하면 안 된다. init.defaultBranch가 설정되지 않은 환경
            // (GitHub Actions 러너 등)에서는 master가 되어 개발 기계에서만 통과하는 테스트가 된다.
            string branchName;
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", originPath);
                branchName = local.Head.FriendlyName;
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("추적"));
            Assert.That(ex.Message, Does.Contain($"git push -u origin {branchName}"),
                "사용자가 그대로 실행할 수 있는 명령을 줘야 합니다");
        }

        [Test]
        public void PushChanges_ReturnsNothingToPush_WhenTheRemoteIsAlreadyUpToDate()
        {
            var (localPath, _) = NewClonedRepoWithBareOrigin();
            var git = NewGitManager("localhost", "testdb", localPath);

            Assert.That(git.PushChanges("localhost", "testdb"), Is.EqualTo(PushResult.NothingToPush));
        }

        [Test]
        public void PushChanges_UpdatesTheRemoteTip_WhenTheLocalBranchIsAhead()
        {
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            var localSha = CommitOneFile(localPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);", "local change");
            var git = NewGitManager("localhost", "testdb", localPath);

            var result = git.PushChanges("localhost", "testdb");

            Assert.That(result, Is.EqualTo(PushResult.Pushed));
            using var origin = new Repository(originPath);
            // 반환값만 보면 push가 아무것도 하지 않아도 통과한다. 원격의 tip을 직접 확인한다.
            Assert.That(origin.Head.Tip.Sha, Is.EqualTo(localSha),
                "Push 후 원격의 tip이 로컬 커밋이어야 합니다");
        }

        [Test]
        public void PushChanges_ThrowsGitPushRejectedException_WhenTheRemoteHasMovedAhead()
        {
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();

            // 다른 사람이 원격에 먼저 올린다.
            var otherPath = NewTempDir();
            Repository.Clone(originPath, otherPath);
            CommitOneFile(otherPath, "dbo/Tables/Other.sql", "CREATE TABLE Other (Id INT);", "other change");
            using (var other = new Repository(otherPath))
            {
                other.Network.Push(other.Head);
            }

            // 우리는 fetch하지 않은 채 우리 커밋을 만든다.
            var localSha = CommitOneFile(localPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);", "local change");
            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<GitPushRejectedException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("거부"));
            Assert.That(ex.Message, Does.Contain("Pull"),
                "무엇을 해야 하는지 알려줘야 합니다");
            Assert.That(ex.Message, Does.Contain("권한"),
                "브랜치 보호·권한도 같은 증상을 내므로 후보로 남겨야 합니다");

            using var local = new Repository(localPath);
            Assert.That(local.Head.Tip.Sha, Is.EqualTo(localSha),
                "Push는 실패해도 로컬 저장소를 변경하지 않아야 합니다");
        }

        [Test]
        public void PushChanges_TellsTheUserToSwitchToSsh_WhenTheRemoteIsHttps()
        {
            var localPath = NewRepoWithCommit();
            string branchName;
            using (var local = new Repository(localPath))
            {
                // 닿지 않는 HTTPS 원격. 접속을 시도하기 전에 판정되는 안내만 확인한다.
                local.Network.Remotes.Add("origin", "https://127.0.0.1:1/nope.git");
                branchName = local.Head.FriendlyName;
                var branch = local.Branches[branchName];
                local.Branches.Update(branch,
                    b => b.Remote = "origin",
                    b => b.UpstreamBranch = $"refs/heads/{branchName}");
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<GitRemoteException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("SSH"),
                "HTTPS 원격에서는 SSH로 바꾸는 방법을 안내해야 합니다");
        }

        [Test]
        public void PushChanges_AddsNoGuidance_WhenTheRemoteIsALocalPath()
        {
            // RemoteDiagnostics가 Other/Unknown에 null을 주므로 무관한 실패에 힌트가 붙지 않아야 한다.
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            CommitOneFile(localPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);", "local change");
            TryDeleteDirectory(originPath);

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<LibGit2SharpException>(() => git.PushChanges("localhost", "testdb"),
                "안내할 것이 없으면 libgit2의 원본 예외가 그대로 전파돼야 합니다");
            Assert.That(ex!.Message, Does.Not.Contain("SSH"));
        }
```

> **주의:** `PushChanges_TellsTheUserToSwitchToSsh_WhenTheRemoteIsHttps` 가 net48에서 멈추면
> (기존 `PullChanges_ThrowsGitAuthenticationException_...` 이 `[Explicit]` 로 밀려난 것과 같은 증상)
> 이 테스트만 `[Explicit]` 로 표시하고 그 사유를 주석으로 남긴 뒤 진행한다. 나머지는 영향받지 않는다.
>
> `PushChanges_ThrowsGitPushRejectedException_WhenTheRemoteHasMovedAhead` 가
> `GitPushRejectedException` 대신 다른 예외로 실패하면, 파일 전송이 거부를
> `NonFastForwardException` 이 아닌 다른 방식으로 낸다는 뜻이다. 그 경우 실제 예외 타입을
> 확인해 Step 4의 catch에 더한다 — **문자열 매칭으로 우회하지 말 것.**

- [x] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~PushChanges"`
Expected: 컴파일 실패 — `PushChanges` 가 없다.

- [x] **Step 4: `IGitManager` 에 선언하고 `GitManager` 에 구현한다**

`src/DBVC.Core/Abstractions.cs` 의 `IGitManager` 에서 `PullChanges` 아래에 한 줄 더한다.

```csharp
        PushResult PushChanges(string serverName, string databaseName);
```

`GitManager` 가 이 인터페이스의 **유일한 구현체**다(테스트는 Moq 목을 쓴다). 다른 곳이 깨지지 않는다.

`src/DBVC.Core/GitManager.cs` 의 `PullChanges` 아래, `BuildPullOptions` 위에 넣는다.

```csharp
        /// <summary>
        /// 현재 브랜치의 커밋을 추적 중인 원격 브랜치에 올린다.
        /// 원격이 ref 갱신을 거부하면 <see cref="GitPushRejectedException"/>을,
        /// 원격이 사용자 자격 증명을 요구하면 <see cref="GitAuthenticationException"/>을,
        /// 그 외에 원격과 통신하지 못했고 안내할 원인이 있으면 <see cref="GitRemoteException"/>을 던진다.
        /// 원격이 없거나 현재 브랜치에 추적 중인 원격 브랜치가 없으면 <see cref="InvalidOperationException"/>을 던진다.
        ///
        /// 이 메서드는 성공하든 실패하든 로컬 저장소와 작업 트리를 변경하지 않는다.
        /// Pull의 AbortMerge에 해당하는 복구 경로가 없는 이유다.
        /// </summary>
        public PushResult PushChanges(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return PushResult.NoMapping;

            using var repo = new Repository(repoPath);

            var guidance = ValidateRemoteAndBuildGuidance(repo, repoPath, "Push");

            // 마지막 fetch 기준의 로컬 값이다. 0이면 올릴 것이 없는 것이 확실하지만
            // (로컬 커밋은 언제나 이 값을 올린다), 0보다 크다고 해서 원격이 뒤처져 있다는
            // 보장은 없다. 헛수고를 줄이는 검사이지 성공/거부 판정의 근거가 아니다.
            if (repo.Head.TrackingDetails.AheadBy == 0) return PushResult.NothingToPush;

            var requiresUserCredentials = false;
            var pushErrors = new List<PushStatusError>();
            var options = BuildPushOptions(
                () => requiresUserCredentials = true,
                error => pushErrors.Add(error));

            try
            {
                repo.Network.Push(repo.Head, options);
            }
            // NonFastForwardException은 LibGit2SharpException의 파생 타입이므로 반드시 먼저 잡는다.
            // 아래 catch에 when (guidance != null) 필터가 붙어 있어 순서가 곧 정확성이다 - 이 catch를
            // 뒤로 옮기면 SSH 원격에서 발생한 거부가 GitPushRejectedException 대신
            // GitRemoteException으로 둔갑한다. PullChanges가 CheckoutConflictException에서 겪은 함정과 같다.
            catch (NonFastForwardException ex)
            {
                throw new GitPushRejectedException(BuildPushRejectionMessage(null), ex);
            }
            catch (LibGit2SharpException ex) when (requiresUserCredentials)
            {
                // 콜백이 호출됐다는 것 자체가 "이 원격은 HTTPS이고 자격 증명을 요구한다"는 신호다.
                // SSH는 시스템 ssh 실행 파일이 처리하므로 이 콜백을 거치지 않는다.
                throw new GitAuthenticationException(
                    $"'{repoPath}' 저장소의 원격이 사용자 자격 증명을 요구합니다." +
                    Environment.NewLine + Environment.NewLine +
                    (guidance ?? CredentialFallbackMessage), ex);
            }
            catch (LibGit2SharpException ex) when (guidance != null)
            {
                throw new GitRemoteException(
                    ex.Message + Environment.NewLine + Environment.NewLine + guidance, ex);
            }

            // 서버가 상태로 거부를 보고하는 경로다(smart 전송 - SSH·HTTPS).
            // 이 검사가 없으면 Network.Push가 정상 반환한 것을 성공으로 읽게 된다.
            if (pushErrors.Count > 0)
            {
                throw new GitPushRejectedException(BuildPushRejectionMessage(pushErrors[0]));
            }

            return PushResult.Pushed;
        }

        /// <summary>
        /// 거부 안내를 만든다. 서버가 준 원문이 있으면 그대로 싣고, 그 아래 원인 후보를 붙인다.
        ///
        /// 서버·libgit2의 메시지를 문자열로 매칭해 원인을 판정하지 않는다 - 버전과 전송 방식에
        /// 따라 달라진다. 대신 후보를 둘로 한정한다. force push를 제공하지 않는 이상
        /// ref 갱신이 거부되는 원인은 실제로 이 둘뿐이다.
        /// </summary>
        private static string BuildPushRejectionMessage(PushStatusError? error)
        {
            var header = error == null
                ? "원격이 Push를 거부했습니다."
                : $"원격이 '{error.Reference}' 갱신을 거부했습니다." +
                  Environment.NewLine + $"서버 응답: {error.Message}";

            return header + Environment.NewLine + Environment.NewLine +
                "원인은 보통 둘 중 하나입니다." + Environment.NewLine +
                "- 원격에 로컬로 가져오지 않은 커밋이 있습니다. Pull을 먼저 하세요." + Environment.NewLine +
                "- 이 브랜치가 보호되어 있거나 밀어넣을 권한이 없습니다.";
        }
```

- [x] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests`
Expected: PASS. 기존 테스트도 전부 통과해야 한다 — Task 1의 헬퍼가 Pull에서 그대로 쓰이고 있다.

- [x] **Step 6: 커밋**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 커밋을 원격에 올리는 PushChanges를 더한다"
```

---

## Task 4: View Changes 창에 Push 버튼을 더한다

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (72-73행 부근)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitManager.PushChanges` → `PushResult` (Task 3)
- Produces: `ICommand ViewChangesViewModel.PushCommand`

- [x] **Step 1: 실패하는 테스트를 쓴다**

`ViewChangesViewModelTests.cs` 의 `// ---------- Commit ----------` 섹션 **위**(Pull 섹션 바로 다음)에 넣는다.

```csharp
        // ---------- Push ----------

        [Test]
        public void PushCommand_IsEnabled_WhenTheDatabaseIsMapped()
        {
            Assert.That(NewConnectedViewModel().PushCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void PushCommand_IsDisabled_WhenTheDatabaseIsNotMapped()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);

            Assert.That(NewConnectedViewModel().PushCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void PushCommand_PushesWithoutAsking()
        {
            // Push는 로컬 저장소도 작업 트리도 건드리지 않는다. Pull의 사전 확인은
            // 병합이 미커밋 변경을 지울 수 있어서인데, 여기엔 그 위험이 없다.
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.Pushed);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.Zero);
            _git.Verify(g => g.PushChanges(Server, Database), Times.Once);
        }

        [Test]
        public void PushCommand_NotifiesOnSuccess()
        {
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.Pushed);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.Infos, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors, Is.Empty);
        }

        [Test]
        public void PushCommand_ReportsNothingToPushAsInformation_NotAnError()
        {
            // 원격이 이미 최신인 것은 정상 상태다. 오류 대화상자를 띄우면 사용자가
            // 무언가 잘못됐다고 읽는다.
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.NothingToPush);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty);
            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("올릴 커밋이 없습니다"));
        }

        [Test]
        public void PushCommand_ReportsAMissingMapping()
        {
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.NoMapping);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Is.EqualTo("DBVC Push 실패"));
        }

        [Test]
        public void PushCommand_ReportsARejection_WithTheExceptionsOwnMessageIntact()
        {
            // Core가 완전한 한국어 안내를 메시지에 담아 던진다. 전용 catch를 두면
            // catch-all과 글자 그대로 같은 코드가 된다 - Pull이 GitAuthenticationException에서
            // 실제로 겪고 제거한 결함이다. 이 테스트는 그 문구가 그대로 나오는지만 지킨다.
            _git.Setup(g => g.PushChanges(Server, Database))
                .Throws(new GitPushRejectedException("원격이 Push를 거부했습니다. Pull을 먼저 하세요."));
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Is.EqualTo("DBVC Push 실패"));
            Assert.That(_notifier.ErrorCalls[0].Message, Is.EqualTo("원격이 Push를 거부했습니다. Pull을 먼저 하세요."));
        }

        [Test]
        public void PushCommand_ReportsAnUnexpectedFailure()
        {
            _git.Setup(g => g.PushChanges(Server, Database))
                .Throws(new InvalidOperationException("추적 중인 원격 브랜치가 없어 Push할 수 없습니다."));
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Message, Does.Contain("추적"));
        }

        [Test]
        public void PushCommand_DoesNotRefresh_AfterASuccessfulPush()
        {
            // Push는 로컬에 아무것도 바꾸지 않는다. Refresh는 SMO 추출을 부르는 비싼 연산이며
            // 여기서 부를 이유가 없다.
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.Pushed);
            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();

            vm.PushCommand.Execute(null);

            _git.Verify(g => g.PushChanges(Server, Database), Times.Once, "Push가 실제로 성공했다는 전제 자체를 확인해야 합니다");
            _smo.Verify(
                s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>?>()),
                Times.Never);
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~PushCommand"`
Expected: 컴파일 실패 — `PushCommand` 가 없다.

- [x] **Step 3: `PushCommand` 를 구현한다**

`ViewChangesViewModel.cs` 생성자에서 `PullCommand = new RelayCommand(Pull, CanPull);` 아래에 한 줄 더한다.

```csharp
            PushCommand = new RelayCommand(Push, CanPush);
```

`public ICommand PullCommand { get; }` 아래에 속성을 더한다.

```csharp
        /// <summary>로컬 저장소의 커밋을 원격 저장소에 올린다.</summary>
        public ICommand PushCommand { get; }
```

`// ---------- Pull ----------` 블록이 끝나는 자리(`// ---------- 저장소 매핑 ----------` 바로 위)에 넣는다.

```csharp
        // ---------- Push ----------

        private bool CanPush() => HasContext && IsMapped;

        /// <summary>
        /// Pull과 달리 사전 확인이 없다 - Push는 로컬 저장소도 작업 트리도 변경하지 않으므로
        /// 사용자가 잃을 것이 없다. 성공 후 Refresh나 History 재적재도 하지 않는다.
        /// 로컬에 바뀐 것이 없기 때문이다.
        /// </summary>
        private void Push()
        {
            if (!CanPush()) return;

            PushResult result;
            try
            {
                result = _gitManager.PushChanges(ServerName!, DatabaseName!);
            }
            catch (Exception ex)
            {
                // GitPushRejectedException은 여기서 잡힌다 - Core가 이미 완전한 한국어 안내를
                // 메시지에 담아 던지므로, 전용 catch를 두면 이 분기와 완전히 같은 코드를
                // 중복할 뿐이다. Pull이 GitAuthenticationException에서 겪은 결함이다. 되살리지 말 것.
                _notifier.ShowError("DBVC Push 실패", ex.Message);
                return;
            }

            switch (result)
            {
                case PushResult.NoMapping:
                    _notifier.ShowError("DBVC Push 실패", "매핑된 Git 저장소를 찾을 수 없습니다.");
                    break;
                case PushResult.NothingToPush:
                    _notifier.ShowInfo("DBVC Push", "올릴 커밋이 없습니다. 원격이 이미 최신입니다.");
                    break;
                case PushResult.Pushed:
                    _notifier.ShowInfo("DBVC Push", "커밋을 원격 저장소에 올렸습니다.");
                    break;
            }
        }
```

`RaiseActionCanExecuteChanged()` 에 한 줄 더한다.

```csharp
            (PushCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

`using DBVC.Core.Models;` 는 이 파일에 **이미 있다**(10행). 더할 것이 없다.

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests`
Expected: PASS

- [x] **Step 5: XAML에 버튼을 더한다**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml` 의 Pull 버튼을 이렇게 바꾼다. **Pull이 갖고 있던 오른쪽 여백 16을 Push로 옮긴다** — Pull·Push가 원격 연산 한 덩어리가 되고 스크립트 버튼과의 구분은 유지된다.

```xml
                <Button Content="Pull" Command="{Binding PullCommand}" Width="70" Margin="0,0,10,4"
                        ToolTip="원격 저장소의 변경을 로컬 저장소로 가져옵니다. 데이터베이스에는 적용하지 않습니다." />
                <Button Content="Push" Command="{Binding PushCommand}" Width="70" Margin="0,0,16,4"
                        ToolTip="로컬 저장소의 커밋을 원격 저장소에 올립니다." />
```

- [x] **Step 6: 전체 빌드와 테스트를 확인한다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests && dotnet test tests/DBVC.Vsix.Tests`
Expected: 빌드 성공, 모든 테스트 PASS

- [x] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): View Changes 창에 Push 버튼을 더한다"
```

---

## Task 5: 문서에서 "Push 없음" 서술을 정정한다

Push가 없다는 전제로 쓰인 곳이 여러 문서에 흩어져 있다. 하나라도 남으면 사용자가 있는 기능을 없다고 읽는다.

**Files:**
- Modify: `README.md` (8행, 14행 부근, 57행 부근)
- Modify: `docs/setup-checklist.md` (295, 297, 345, 434, 460행 부근)
- Modify: `docs/superpowers/specs/2026-08-03-dbvc-ssh-first-git-auth-design.md` (32행)

- [x] **Step 1: 남아 있는 언급을 전부 찾는다**

```bash
grep -rn "Push\|push" README.md docs/setup-checklist.md docs/superpowers/specs/2026-08-03-dbvc-ssh-first-git-auth-design.md
```

아래 편집을 마친 뒤 이 명령을 다시 돌려, 남은 것이 CI 트리거(`push:`)와 추적 브랜치 안내(`git push -u`)뿐인지 확인한다. 그 둘은 **바꾸지 않는다** — 전자는 GitHub Actions 설정이고 후자는 DBVC가 추적을 대신 설정하지 않기로 한 결정의 결과다.

- [x] **Step 2: `README.md` 를 고친다**

8행의 Git 통합 설명에 push를 더한다.

```markdown
- **Git 통합 (LibGit2Sharp):** 내보낸 `.sql` 파일들을 Git 저장소에 스테이징(Staging) 및 커밋(Commit)하고, 원격 저장소에 올릴 수 있는 완벽한 형상 관리 기능을 제공합니다.
```

14행 **아래**에 항목을 더한다.

```markdown
- **Git Push:** 로컬 저장소의 커밋을 원격 저장소에 올립니다. 원격이 앞서 있으면 거부 사유를 알리고 멈추며, 로컬 저장소는 그대로 둡니다.
```

57행의 "원격 변경 가져오기" 항목 **아래**에 동작 설명을 더한다.

```markdown
- **원격에 올리기:** **Push** 는 로컬 저장소의 커밋을 원격에 올립니다. 로컬 저장소와 작업 트리는 건드리지 않으므로 실패해도 잃을 것이 없습니다. 원격에 먼저 올라간 커밋이 있으면 거부되며, 이때는 **Pull** 로 받아 병합한 뒤 다시 누르면 됩니다.
```

- [x] **Step 3: `docs/setup-checklist.md` 를 고친다**

295-297행의 체크 항목을 바꾼다.

```markdown
- [x] 원격에 올린다. DBVC의 **Push** 버튼을 누른다.
```

바로 아래 `git -C ... push` 코드 블록은 지운다.

345행을 바꾼다.

```markdown
- [x] **5단계를 운영 PC에서 반복한다** (Setup DBVC → Refresh → Commit → Push → Pull).
```

434행의 "알아둘 것" 항목을 바꾼다.

```markdown
- **Push는 커밋만 올린다.** 로컬 저장소와 작업 트리는 변하지 않으므로 실패해도 잃을 것이 없다.
  원격이 앞서 있으면 거부되며, Pull로 받아 병합한 뒤 다시 누른다. force push는 제공하지 않는다.
```

460행의 문제 해결 표 행을 바꾼다.

```markdown
| 커밋했는데 원격에 없다 | 커밋과 Push는 별개다. **Push** 버튼을 누른다 |
```

그 아래에 행을 하나 더한다.

```markdown
| Push가 거부된다 | 원격에 먼저 올라간 커밋이 있다. **Pull** 로 받아 병합한 뒤 다시 Push. 그래도 거부되면 브랜치 보호·권한을 확인 |
```

- [x] **Step 4: 이전 설계 문서에 후속 표기를 남긴다**

`docs/superpowers/specs/2026-08-03-dbvc-ssh-first-git-auth-design.md` 32행을 바꾼다.

```markdown
* **DBVC가 네트워크를 쓰는 지점은 Pull 하나뿐이다.** Push·Clone·Fetch API가 없다.
  (2026-08-18 갱신: Push가 추가되었다. [2026-08-18-dbvc-git-push-design.md](2026-08-18-dbvc-git-push-design.md) 참조.
  인증 경로는 이 문서가 정한 그대로이며, `RemoteDiagnostics`·`SshExecutableLocator`를 그대로 재사용한다.)
```

- [x] **Step 5: 남은 언급을 확인한다**

Step 1의 `grep` 을 다시 돌린다.
Expected: `push:`(CI 트리거)와 `git push -u origin`(추적 설정 안내)만 남는다.

- [x] **Step 6: 커밋**

```bash
git add README.md docs/setup-checklist.md docs/superpowers/specs/2026-08-03-dbvc-ssh-first-git-auth-design.md
git commit -m "docs: Push가 생긴 사실을 README와 도입 체크리스트에 반영한다"
```

---

## 수동 검증 (구현 후, SSMS 21 실행 환경)

단위 테스트가 닿지 못하는 것들이다. CI는 WPF 렌더링·VS 패키지 로딩·실제 원격 통신을 검증하지 않는다.

- [x] `.vsix` 를 빌드해 SSMS 21에 설치하고 View Changes 창에 **Push 버튼이 보이는지** 확인한다. 창을 좁게 도킹했을 때 `WrapPanel` 이 줄바꿈하며 버튼이 잘리지 않는지도 함께 본다.
- [x] 커밋 후 Push → GitHub(SSH 원격)에 반영됐는지 확인한다. **이것이 `OnPushStatusError` 배선을 지나는 유일한 검증이다** — 단위 테스트는 파일 전송(`NonFastForwardException`) 경로만 덮는다.
- [x] 원격에 다른 곳에서 먼저 커밋을 올린 뒤 Push → 거부 안내가 뜨는지, 로컬 커밋이 그대로 남아 있는지.
- [x] 올릴 것이 없는 상태에서 Push → 오류 대화상자가 아니라 정보 안내인지.
- [x] 폐쇄망 PC에서 SSH로 GitLab에 Push.
