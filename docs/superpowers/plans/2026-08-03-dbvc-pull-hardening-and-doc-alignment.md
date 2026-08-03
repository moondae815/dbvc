# DBVC Pull 견고화 및 문서·코드 정합화 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pull 실패 경로를 libgit2 영문 원문 대신 도메인 예외로 표현하고, 설계 문서와 코드가 어긋난 8곳을 정리한다.

**Architecture:** `GitManager.PullChanges`가 `Commands.Pull` 호출만 `try`로 감싸 `CheckoutConflictException`을 `WorkingTreeConflictException`으로, 자격 증명 요구를 `GitAuthenticationException`으로 변환한다. Vsix는 catch-all 대신 예외 타입별로 문구를 고른다. 스크립트 생성 결과는 경고 배너가 아니라 `IUserNotifier.ShowInfo`로 알리고, 제외된 객체는 생성된 `.sql` 헤더에도 남긴다.

**Tech Stack:** .NET Framework 4.8 / .NET Standard 2.0, LibGit2Sharp 0.32, WPF MVVM, NUnit 4, Moq

## Global Constraints

* `DBVC.Core`는 `net48;netstandard2.0` 멀티타깃이다. WPF·VS SDK에 의존하는 코드를 넣지 않는다.
* `DBVC.Vsix`는 `net48` 전용이다.
* macOS/Linux에서는 `net10.0` 타깃만 실행된다. 모든 테스트 명령에 `-f net10.0`을 붙인다. `net48` 테스트는 Windows CI가 돌린다.
* 사용자에게 보이는 모든 문구는 한국어다. 기존 문구의 어투(평서형 "…합니다")를 따른다.
* `CheckoutConflictException`은 `LibGit2SharpException`의 파생 타입이다. 파생 타입을 먼저 잡는다 — 명확성을 위한 선택이지, 정확성을 위해 필수는 아니다. `LibGit2SharpException` catch에는 `when (requiresUserCredentials)` 필터가 있어 C#이 순서를 강제하지 않으며, 자격 증명을 요구하는 원격은 체크아웃 이전 fetch 단계에서 이미 실패하므로 `CheckoutConflictException`이 던져지는 시점에는 `requiresUserCredentials`가 항상 `false`다 — 순서를 반대로 해도 겹치는 미커밋 변경이 인증 오류로 잘못 보고되지 않는다.
* `CheckoutConflictException` 경로에서는 `AbortMerge`를 호출하지 않는다. 병합이 시작되지 않았으므로 되돌릴 것이 없다. (실측으로 확인됨: HEAD 불변, 인덱스 충돌 0, 로컬 미커밋 내용 보존)
* `WarningMessage`는 지속 상태(매핑 안 됨, SMO 추출 실패) 전용이다. 일회성 동작의 결과를 여기에 쓰지 않는다.
* 커밋 메시지는 한국어 제목 + Conventional Commits 접두사(`feat:`, `fix:`, `test:`, `docs:`, `refactor:`)를 쓴다.

---

## File Structure

**생성**

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/WorkingTreeConflictException.cs` | 병합 체크아웃이 거부됨. 저장소는 그대로 |
| `src/DBVC.Core/GitAuthenticationException.cs` | 원격이 사용자 자격 증명을 요구함 |

**수정**

| 파일 | 변경 |
| --- | --- |
| `src/DBVC.Core/GitManager.cs` | `PullChanges` 예외 변환, `ResolveCredentials` 추가 |
| `src/DBVC.Core/ScriptGenerator.cs` | `BuildScript`에 `excludedObjects` 인자, 헤더에 `Excluded` 줄 |
| `src/DBVC.Core/ScriptExporter.cs` | 제외 목록을 `BuildScript`에 전달 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | Pull 오류 분기, 확인 문구, `GenerateScript` 알림 |
| `docs/superpowers/specs/2026-07-31-dbvc-view-changes-design.md` | P3 #8, #9 |
| `docs/superpowers/specs/2026-07-31-dbvc-ssms21-plugin-design.md` | P3 #10, #13 |
| `docs/superpowers/specs/2026-08-01-dbvc-script-generation-design.md` | P3 #11, #12 + 3.1·4절 |
| `README.md` | P3 #13 |

**테스트**

| 파일 | 대상 |
| --- | --- |
| `tests/DBVC.Core.Tests/GitManagerTests.cs` | `ResolveCredentials`, `PullChanges` 예외 변환 |
| `tests/DBVC.Core.Tests/ScriptGeneratorTests.cs` | `Excluded` 헤더 |
| `tests/DBVC.Core.Tests/ScriptExporterTests.cs` | 제외 목록 전달 |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | Pull 분기, 스크립트 알림 |

---

## Task 1: Core — Pull 예외 변환과 자격 증명 핸들러

**Files:**
- Create: `src/DBVC.Core/WorkingTreeConflictException.cs`
- Create: `src/DBVC.Core/GitAuthenticationException.cs`
- Modify: `src/DBVC.Core/GitManager.cs:176-207` (`PullChanges`)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: 기존 `GitManager.ResolveRepoPath`, `BuildSignature`, `AbortMerge` (모두 `private static` 또는 `private`)
- Produces:
  - `public class WorkingTreeConflictException : Exception` — 생성자 `(string message)`, `(string message, Exception innerException)`
  - `public class GitAuthenticationException : Exception` — 생성자 동일
  - `internal static Credentials GitManager.ResolveCredentials(SupportedCredentialTypes types, out bool requiresUserCredentials)`
  - `PullChanges`는 계속 `bool`을 반환하고, 매핑이 없으면 `false`, 원격이 없으면 `InvalidOperationException`, 병합 충돌이면 `MergeConflictException`을 던진다 (기존과 동일)

**배경 (실측 결과).** 미커밋 변경이 받아올 변경과 겹치는 상태에서 `Commands.Pull`을 부르면
`LibGit2Sharp.CheckoutConflictException`("1 conflict prevents checkout")이 던져지고,
HEAD는 그대로이며, 인덱스 충돌은 0개이고, 로컬의 미커밋 내용은 파일에 그대로 남는다.
`CheckoutConflictException`의 기반 타입은 `NativeException`이고 이는 `LibGit2SharpException`을 상속한다.
`SupportedCredentialTypes`는 `UsernamePassword = 1`, `Default = 2`인 `[Flags]` 열거형이다.

- [ ] **Step 1: 예외 타입 두 개를 만든다**

`src/DBVC.Core/WorkingTreeConflictException.cs`:

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// 받아올 변경과 겹치는 미커밋 변경 때문에 병합 체크아웃이 거부되어 Pull을 하지 못했음을 알린다.
    /// <see cref="MergeConflictException"/>과 달리 병합이 시작조차 되지 않았으므로
    /// 저장소는 손대지 않은 그대로이고 잃은 것이 없다.
    /// </summary>
    public class WorkingTreeConflictException : Exception
    {
        public WorkingTreeConflictException(string message) : base(message)
        {
        }

        public WorkingTreeConflictException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
```

`src/DBVC.Core/GitAuthenticationException.cs`:

```csharp
using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격이 사용자 자격 증명(사용자명/암호·토큰)을 요구했으나 DBVC는 Windows 통합 인증만 지원한다.
    /// </summary>
    public class GitAuthenticationException : Exception
    {
        public GitAuthenticationException(string message) : base(message)
        {
        }

        public GitAuthenticationException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
```

- [ ] **Step 2: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs`의 `// ---------- PullChanges ----------` 구역 끝
(`PullChanges_ThrowsMergeConflictException_AndRestoresHead_OnConflict` 다음)에 추가한다.

```csharp
        [Test]
        public void ResolveCredentials_UsesWindowsIntegratedAuth_WhenTheRemoteSupportsIt()
        {
            var credentials = GitManager.ResolveCredentials(
                SupportedCredentialTypes.UsernamePassword | SupportedCredentialTypes.Default,
                out var requiresUserCredentials);

            Assert.That(credentials, Is.InstanceOf<DefaultCredentials>());
            Assert.That(requiresUserCredentials, Is.False,
                "Default를 지원하는 원격은 통합 인증으로 처리되므로 자격 증명 요구로 표시하면 안 됩니다");
        }

        [Test]
        public void ResolveCredentials_FlagsTheRemote_WhenOnlyUsernamePasswordIsSupported()
        {
            var credentials = GitManager.ResolveCredentials(
                SupportedCredentialTypes.UsernamePassword,
                out var requiresUserCredentials);

            Assert.That(credentials, Is.InstanceOf<DefaultCredentials>(),
                "핸들러는 Credentials를 반드시 돌려줘야 합니다. 여기서 하는 일은 실패를 막는 것이 아니라 원인을 기록하는 것입니다");
            Assert.That(requiresUserCredentials, Is.True,
                "Default를 지원하지 않으면 GitAuthenticationException으로 감쌀 근거가 됩니다");
        }

        [Test]
        public void PullChanges_ThrowsWorkingTreeConflictException_WhenUncommittedChangesOverlapTheIncomingOnes()
        {
            var originPath = NewRepoWithCommit();
            var clonePath = NewTempDir();
            Repository.Clone(originPath, clonePath);

            // 원격이 파일을 바꿔 커밋한다.
            WriteRepoFile(originPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, RemoteCol INT);");
            using (var origin = new Repository(originPath))
            {
                Commands.Stage(origin, "*");
                origin.Commit("remote edit", TestSignature, TestSignature);
            }

            // 로컬은 같은 파일을 커밋하지 않은 채 수정한다. 충돌 커밋이 아니라 미커밋 변경이다.
            const string localContent = "CREATE TABLE Users (Id INT, LocalUncommitted INT);";
            WriteRepoFile(clonePath, "dbo/Tables/Users.sql", localContent);

            string headBefore;
            using (var clone = new Repository(clonePath))
            {
                headBefore = clone.Head.Tip.Sha;
            }

            var git = NewGitManager("localhost", "testdb", clonePath);

            var ex = Assert.Throws<WorkingTreeConflictException>(() => git.PullChanges("localhost", "testdb"));

            Assert.That(ex!.InnerException, Is.InstanceOf<CheckoutConflictException>(),
                "원인을 보존해야 진단할 수 있습니다");
            Assert.That(ex.Message, Does.Contain("저장소는 변경되지 않았습니다"),
                "이 경로의 핵심 정보는 '잃은 것이 없다'는 사실입니다");

            using (var clone = new Repository(clonePath))
            {
                Assert.That(clone.Head.Tip.Sha, Is.EqualTo(headBefore),
                    "병합이 시작되지 않았으므로 HEAD가 움직이면 안 됩니다");
                Assert.That(clone.Index.Conflicts, Is.Empty,
                    "AbortMerge를 부르지 않아도 저장소가 병합 중 상태로 남지 않아야 합니다");
            }

            Assert.That(File.ReadAllText(Path.Combine(clonePath, "dbo", "Tables", "Users.sql")),
                Is.EqualTo(localContent),
                "미커밋 변경이 그대로 남아 있어야 합니다. 이것이 MergeConflictException과 갈리는 지점입니다");
        }
```

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "ResolveCredentials|WorkingTreeConflict"`

Expected: 컴파일 실패. `GitManager.ResolveCredentials`와 `WorkingTreeConflictException`이 없다.
(예외 타입은 Step 1에서 만들었으므로 `ResolveCredentials`만 없는 상태여야 한다.)

- [ ] **Step 4: `ResolveCredentials`를 추가한다**

`src/DBVC.Core/GitManager.cs`의 `PullChanges` 바로 아래에 넣는다.

```csharp
        /// <summary>
        /// 원격이 요구하는 자격 증명 종류를 보고 무엇을 넘길지 정한다.
        /// DBVC는 Windows 통합 인증(NTLM/Kerberos)만 지원하므로, 그 외를 요구하는 원격은
        /// <paramref name="requiresUserCredentials"/>로 표시만 하고 실패하게 둔다.
        /// libgit2의 인증 오류 메시지는 버전·전송 방식에 따라 달라져 문자열로 매칭할 수 없다.
        /// 대신 핸들러가 호출되는 시점에 원인을 기록해 두고, 예외를 감쌀 때 그 기록을 쓴다.
        /// </summary>
        internal static Credentials ResolveCredentials(
            SupportedCredentialTypes types,
            out bool requiresUserCredentials)
        {
            requiresUserCredentials = !types.HasFlag(SupportedCredentialTypes.Default);
            return new DefaultCredentials();
        }
```

- [ ] **Step 5: `PullChanges`를 고친다**

`Commands.Pull` 한 줄을 아래로 교체한다. `MergeStatus.Conflicts` 분기와 `AbortMerge`는 건드리지 않는다.

기존:

```csharp
            var result = Commands.Pull(repo, signature, new PullOptions());
```

교체:

```csharp
            // 핸들러가 "이 원격은 사용자 자격 증명을 요구한다"고 알려 주면 여기에 기록된다.
            var requiresUserCredentials = false;
            var options = new PullOptions
            {
                FetchOptions = new FetchOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) =>
                    {
                        var credentials = ResolveCredentials(types, out var needsUserCredentials);
                        if (needsUserCredentials) requiresUserCredentials = true;
                        return credentials;
                    }
                }
            };

            MergeResult result;
            try
            {
                result = Commands.Pull(repo, signature, options);
            }
            // CheckoutConflictException은 LibGit2SharpException의 파생 타입이다. 반드시 먼저 잡는다.
            catch (CheckoutConflictException ex)
            {
                // 병합 체크아웃이 시작조차 거부된 상태다. AbortMerge를 부르면 안 된다 - 되돌릴 것이 없고,
                // hard reset은 오히려 사용자의 미커밋 변경을 지운다.
                throw new WorkingTreeConflictException(
                    $"'{repoPath}' 저장소에 받아올 변경과 겹치는 미커밋 변경이 있어 Pull하지 않았습니다. " +
                    "저장소는 변경되지 않았습니다. 해당 변경을 커밋하거나 되돌린 뒤 다시 시도하세요.", ex);
            }
            catch (LibGit2SharpException ex) when (requiresUserCredentials)
            {
                throw new GitAuthenticationException(
                    $"'{repoPath}' 저장소의 원격이 사용자 자격 증명을 요구합니다. " +
                    "DBVC는 Windows 통합 인증만 지원하므로, SSH 키를 사용하거나 " +
                    "원격 URL에 액세스 토큰을 포함해 다시 시도하세요.", ex);
            }
```

`PullChanges`의 XML 주석도 갱신한다.

```csharp
        /// <summary>
        /// 원격 저장소의 변경을 병합한다.
        /// 병합 중 충돌하면 병합을 되돌리고 <see cref="MergeConflictException"/>을,
        /// 겹치는 미커밋 변경으로 병합이 시작조차 못 하면 <see cref="WorkingTreeConflictException"/>을,
        /// 원격이 사용자 자격 증명을 요구하면 <see cref="GitAuthenticationException"/>을 던진다.
        /// </summary>
```

- [ ] **Step 6: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`

Expected: 전부 PASS. 기존 `PullChanges_FastForwards_WhenRemoteHasNewCommits`와
`PullChanges_ThrowsMergeConflictException_AndRestoresHead_OnConflict`도 계속 통과해야 한다
— 로컬 경로 원격은 자격 증명을 요구하지 않으므로 핸들러가 호출되지 않는다.

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Core/WorkingTreeConflictException.cs src/DBVC.Core/GitAuthenticationException.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): Pull의 체크아웃 거부와 자격 증명 요구를 도메인 예외로 변환"
```

---

## Task 2: Vsix — Pull 오류 문구를 예외 타입별로 분기

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:252-301` (`Pull`)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1의 `WorkingTreeConflictException`, `GitAuthenticationException` (둘 다 `DBVC.Core` 네임스페이스)
- Produces: 없음. `Pull`은 계속 `private void`이고 `PullCommand`의 시그니처도 그대로다.

**배경.** 지금은 catch-all이 모든 예외에 "받아올 변경과 겹치는 미커밋 변경이 있으면 Pull이 거부될 수 있습니다"를
덧붙인다. 원격 미설정 같은 무관한 오류에도 이 힌트가 붙는다. Task 1이 원인을 타입으로 갈랐으므로 추측이 필요 없다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`// ---------- Pull ----------` 구역의 `PullCommand_ReportsAnUnexpectedFailure` 다음에 추가한다.

```csharp
        [Test]
        public void PullCommand_ReportsARejectedCheckout_WithoutClaimingAnythingWasLost()
        {
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new WorkingTreeConflictException(
                    "겹치는 미커밋 변경이 있어 Pull하지 않았습니다. 저장소는 변경되지 않았습니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Does.Contain("중단"),
                "아무 일도 일어나지 않았으므로 '실패'가 아니라 '중단'입니다");
            Assert.That(_notifier.ErrorCalls[0].Message, Does.Contain("변경되지 않았습니다"));
            Assert.That(_notifier.Infos, Is.Empty);
        }

        [Test]
        public void PullCommand_ReportsAnAuthenticationFailure()
        {
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new GitAuthenticationException("원격이 사용자 자격 증명을 요구합니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Does.Contain("실패"));
            Assert.That(_notifier.ErrorCalls[0].Message, Does.Contain("자격 증명"));
            Assert.That(_notifier.Infos, Is.Empty);
        }

        [Test]
        public void PullCommand_TellsTheUserThatARejectedPullLosesNothing_BeforeAsking()
        {
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>()))
                .Returns(new List<string> { "dbo/Tables/Users.sql" });
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(true);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ConfirmCalls[0].Message, Does.Contain("저장소는 그대로입니다"),
                "거부 경로는 무손실입니다. 두 결과를 뭉뚱그리면 사용자가 필요 이상으로 겁먹습니다");
            Assert.That(_notifier.ConfirmCalls[0].Message, Does.Contain("사라질 수 있습니다"),
                "충돌 경로의 손실 가능성은 여전히 알려야 합니다");
        }
```

기존 `PullCommand_ReportsAnUnexpectedFailure`(`:656`)를 강화한다. 새 테스트를 따로 만들지 않는다 —
같은 상황을 두 번 세팅하게 된다. 단언 세 줄을 아래로 교체한다.

```csharp
            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Is.EqualTo("원격(remote)이 설정되어 있지 않습니다."),
                "원인이 타입으로 갈렸으므로 무관한 오류에 미커밋 변경 힌트를 덧붙이면 안 됩니다. 원문만 그대로 보여줍니다");
            Assert.That(_notifier.Infos, Is.Empty,
                "예기치 못한 실패인데 성공 알림까지 뜨면 안 됩니다 - catch 끝의 return이 지워지면 실패해야 합니다");
```

`RecordingNotifier`에 `ConfirmCalls`를 추가한다 (`ViewChangesViewModelTests.cs:1041-1071`).
`ConfirmCallCount`는 기존 테스트가 쓰므로 남겨 둔다.

```csharp
            /// <summary>Confirm에 실제로 전달된 (title, message) 쌍. 문구 자체를 검증할 때 쓴다.</summary>
            public List<(string Title, string Message)> ConfirmCalls { get; } = new List<(string, string)>();
```

`Confirm` 본문을 고친다.

```csharp
            public bool Confirm(string title, string message)
            {
                ConfirmCallCount++;
                ConfirmCalls.Add((title, message));
                return ConfirmResult;
            }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "PullCommand"`

Expected: 새 테스트 3개와 강화한 `ReportsAnUnexpectedFailure`가 FAIL.
`ReportsARejectedCheckout`과 `ReportsAnAuthenticationFailure`는 catch-all이 잡아 타이틀이 "실패"라서,
`ReportsAnUnexpectedFailure`는 힌트가 붙어 원문과 다르기 때문에,
`TellsTheUserThatARejectedPullLosesNothing`은 확인 문구가 달라서 실패한다.

- [ ] **Step 3: `Pull`의 확인 문구와 catch 분기를 고친다**

`ViewChangesViewModel.cs`의 `Pull` 메서드에서 확인 문구 블록을 교체한다.

기존 (`:259-272`):

```csharp
            // GetChangedFiles는 미추적 파일도 포함하지만, 충돌 시 AbortMerge의 hard reset은
            // 미추적 파일을 건드리지 않는다. 그리고 미커밋 변경이 있으면 병합 자체가 거부될 수도 있다
            // (LibGit2Sharp가 병합 상태를 만들기 전에 CheckoutConflictException을 던지는 경우).
            // 그래서 "이 개수만큼 사라진다"고 단정하지 않고 두 가능성을 모두 알린다.
            var pending = _gitManager.GetChangedFiles(mapping.GitPath);
            if (pending.Count > 0)
            {
                var proceed = _notifier.Confirm(
                    "DBVC Pull",
                    $"커밋하지 않은 변경 {pending.Count}개가 있습니다." + Environment.NewLine +
                    "받아올 변경과 겹치면 Pull이 거부되거나, 병합이 진행되다 충돌해 되돌아가면서" + Environment.NewLine +
                    "추적 중인 파일의 변경이 함께 사라질 수 있습니다." + Environment.NewLine +
                    "(DBVC가 추출한 내용은 Refresh로 다시 만들 수 있습니다)" + Environment.NewLine + Environment.NewLine +
                    "계속하시겠습니까?");

                // 취소는 오류가 아니다.
                if (!proceed) return;
            }
```

교체:

```csharp
            // GetChangedFiles는 미추적 파일도 포함하므로 이 개수가 곧 손실량은 아니다.
            // 문구가 개수를 손실량으로 단정하지 않도록 두 결과를 분리해 알린다.
            var pending = _gitManager.GetChangedFiles(mapping.GitPath);
            if (pending.Count > 0)
            {
                var proceed = _notifier.Confirm(
                    "DBVC Pull",
                    $"커밋하지 않은 변경 {pending.Count}개가 있습니다." + Environment.NewLine +
                    "받아올 변경과 겹치면 Pull이 거부됩니다. 이 경우 저장소는 그대로입니다." + Environment.NewLine +
                    "겹치지 않더라도 병합 중 충돌이 나면 병합을 되돌리면서" + Environment.NewLine +
                    "추적 중인 파일의 변경이 함께 사라질 수 있습니다." + Environment.NewLine +
                    "(DBVC가 추출한 내용은 Refresh로 다시 만들 수 있습니다)" + Environment.NewLine + Environment.NewLine +
                    "계속하시겠습니까?");

                // 취소는 오류가 아니다.
                if (!proceed) return;
            }
```

이어서 catch 블록을 교체한다.

기존 (`:286-301`):

```csharp
            catch (MergeConflictException ex)
            {
                // GitManager가 이미 병합을 되돌렸고 안내 문구도 담고 있다.
                _notifier.ShowError("DBVC Pull 중단", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                // 예: 미커밋 변경이 받아올 변경과 겹치면 병합 상태가 만들어지기도 전에
                // libgit2가 원문 메시지로 예외를 던진다. 원인을 특정해 감싸는 작업은
                // DBVC.Core의 후속 과제이므로, 여기서는 흔한 원인을 안내만 덧붙인다.
                var hint = "받아올 변경과 겹치는 미커밋 변경이 있으면 Pull이 거부될 수 있습니다." + Environment.NewLine +
                    "해당 변경을 커밋하거나 되돌린 뒤 다시 시도하세요.";
                _notifier.ShowError("DBVC Pull 실패", ex.Message + Environment.NewLine + Environment.NewLine + hint);
                return;
            }
```

교체:

```csharp
            catch (MergeConflictException ex)
            {
                // GitManager가 이미 병합을 되돌렸고 안내 문구도 담고 있다.
                _notifier.ShowError("DBVC Pull 중단", ex.Message);
                return;
            }
            catch (WorkingTreeConflictException ex)
            {
                // 병합이 시작조차 못 했다. 사용자 관점에서 아무 일도 일어나지 않았으므로 '중단'이다.
                _notifier.ShowError("DBVC Pull 중단", ex.Message);
                return;
            }
            catch (GitAuthenticationException ex)
            {
                _notifier.ShowError("DBVC Pull 실패", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                // 원인이 타입으로 갈렸으므로 흔한 원인을 추측해 덧붙이지 않는다.
                _notifier.ShowError("DBVC Pull 실패", ex.Message);
                return;
            }
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`

Expected: 전부 PASS. `PullCommand_ReportsAMergeConflict`도 계속 통과해야 한다 — 그 분기는 건드리지 않았다.

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "fix(vsix): Pull 오류를 예외 타입별로 안내하고 확인 문구에서 두 결과를 분리"
```

---

## Task 3: Core — 스크립트 헤더에 제외된 객체 기록

**Files:**
- Modify: `src/DBVC.Core/ScriptGenerator.cs:17-50`
- Modify: `src/DBVC.Core/ScriptExporter.cs:65-68`
- Test: `tests/DBVC.Core.Tests/ScriptGeneratorTests.cs`
- Test: `tests/DBVC.Core.Tests/ScriptExporterTests.cs`

**Interfaces:**
- Consumes: 기존 `ScriptExportResult.ExcludedObjects` (`List<string>`, `IReadOnlyCollection<string>`을 만족)
- Produces:
  - `public static string ScriptGenerator.BuildScript(IEnumerable<ScriptSection>? sections, ScriptKind kind, DateTimeOffset generatedAt, IReadOnlyCollection<string>? excludedObjects = null)`
  - 헤더에 `   Excluded: 2 (dbo.A, dbo.B)` 줄이 추가된다. 제외가 없으면 줄 자체가 없다.

**배경.** script-generation 설계 3.1은 "빈 섹션을 건너뛴 사실을 헤더에 기록"하라고 했으나,
`ScriptExporter`가 빈 SQL을 미리 걸러 `BuildScript`에 넘긴다(`ScriptExporter.cs:50-55`).
`ScriptGenerator`의 자체 빈 섹션 필터는 프로덕션 경로에서 발동하지 않으므로 그 개수는 항상 0이다.
기록할 가치가 있는 것은 `ScriptExporter`가 아는 제외된 객체다.
알림은 닫으면 사라지지만 헤더는 파일과 함께 남는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/ScriptGeneratorTests.cs`의 `// ---------- 헤더 ----------` 구역
`BuildScript_LabelsRollbackScriptsDistinctly` 다음에 추가한다.

```csharp
        [Test]
        public void BuildScript_RecordsExcludedObjectsInTheHeader()
        {
            var script = ScriptGenerator.BuildScript(
                new[] { Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);") },
                ScriptKind.Rollback,
                GeneratedAt,
                new[] { "dbo.Gone", "dbo.AlsoGone" });

            Assert.That(script, Does.Contain("Objects: 1"));
            Assert.That(script, Does.Contain("Excluded: 2 (dbo.Gone, dbo.AlsoGone)"),
                "알림은 닫으면 사라지지만 헤더는 파일과 함께 남습니다");
        }

        [Test]
        public void BuildScript_OmitsTheExcludedLine_WhenNothingWasExcluded()
        {
            var withNull = Build(Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);"));
            var withEmpty = ScriptGenerator.BuildScript(
                new[] { Section("dbo.Users", "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);") },
                ScriptKind.Deployment,
                GeneratedAt,
                Array.Empty<string>());

            Assert.That(withNull, Does.Not.Contain("Excluded"),
                "인자를 생략한 기존 호출부의 출력이 달라지면 안 됩니다");
            Assert.That(withEmpty, Does.Not.Contain("Excluded"));
            Assert.That(withEmpty, Is.EqualTo(withNull),
                "빈 목록과 null은 같은 결과를 내야 합니다");
        }
```

`tests/DBVC.Core.Tests/ScriptExporterTests.cs`에는 이미 같은 상황을 만드는 테스트가 있다.
새로 만들지 말고 **그 테스트를 고친다.** 마지막 단언(`:116`)이 이번 변경으로 **의도적으로 깨진다** —
제외된 이름이 스크립트에 없어야 한다고 단언하는데, 헤더에 기록하면 그게 뒤집힌다.

기존 `Export_Deployment_ExcludesObjectsWhoseFileIsMissing`의 단언 세 줄(`:114-116`):

```csharp
            Assert.That(result.IncludedCount, Is.EqualTo(1));
            Assert.That(result.ExcludedObjects, Is.EqualTo(new[] { "dbo.Gone" }));
            Assert.That(result.Script, Does.Not.Contain("dbo.Gone"));
```

교체:

```csharp
            Assert.That(result.IncludedCount, Is.EqualTo(1));
            Assert.That(result.ExcludedObjects, Is.EqualTo(new[] { "dbo.Gone" }));
            Assert.That(result.Script, Does.Not.Contain("/* ---- dbo.Gone"),
                "제외된 객체의 본문 섹션은 들어가면 안 됩니다 - 원래 이 단언이 지키려던 것입니다");
            Assert.That(result.Script, Does.Contain("Excluded: 1 (dbo.Gone)"),
                "다만 무엇이 빠졌는지는 헤더에 남아야 합니다. ScriptExporter가 제외 목록을 전달하지 않으면 실패합니다");
```

> 이 파일에는 `Target(qualifiedName, relativePath)`와 `NewExporter()` 헬퍼가 이미 있다.
> 다른 테스트를 새로 쓸 일이 있으면 그 헬퍼를 쓴다.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "Exclu"`

Expected: 컴파일 실패. `BuildScript`가 인자 4개를 받지 않는다.
컴파일이 통과하도록 임시로 손대지 말고 Step 3~4를 그대로 진행한다.

- [ ] **Step 3: `BuildScript`와 `AppendHeader`를 고친다**

`src/DBVC.Core/ScriptGenerator.cs`:

```csharp
        /// <summary>
        /// 섹션들을 정해진 순서로 병합해 단일 스크립트 텍스트를 만든다.
        /// 내용이 빈 섹션은 제외되며 헤더의 개수에도 반영되지 않는다.
        /// <paramref name="excludedObjects"/>는 호출자(<see cref="ScriptExporter"/>)가 판정한 제외 목록이며,
        /// 파일을 나중에 열어 볼 사람이 무엇이 빠졌는지 알 수 있도록 헤더에 남긴다.
        /// </summary>
        public static string BuildScript(
            IEnumerable<ScriptSection>? sections,
            ScriptKind kind,
            DateTimeOffset generatedAt,
            IReadOnlyCollection<string>? excludedObjects = null)
        {
            var ordered = (sections ?? Enumerable.Empty<ScriptSection>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Sql))
                .OrderBy(GetTypeSortOrder)
                .ThenBy(s => s.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var builder = new StringBuilder();
            AppendHeader(builder, kind, generatedAt, ordered.Count, excludedObjects);

            foreach (var section in ordered)
            {
                AppendSection(builder, section);
            }

            return builder.ToString();
        }

        private static void AppendHeader(
            StringBuilder builder,
            ScriptKind kind,
            DateTimeOffset generatedAt,
            int objectCount,
            IReadOnlyCollection<string>? excludedObjects)
        {
            var title = kind == ScriptKind.Rollback ? "DBVC Rollback Script" : "DBVC Deployment Script";

            builder.AppendLine("/* ============================================================");
            builder.AppendLine($"   {title}");
            builder.AppendLine($"   Generated: {generatedAt:yyyy-MM-ddTHH:mm:sszzz}");
            builder.AppendLine($"   Objects: {objectCount}");

            if (excludedObjects != null && excludedObjects.Count > 0)
            {
                builder.AppendLine($"   Excluded: {excludedObjects.Count} ({string.Join(", ", excludedObjects)})");
            }

            builder.AppendLine("   ============================================================ */");
            builder.AppendLine();
        }
```

- [ ] **Step 4: `ScriptExporter`가 제외 목록을 넘기게 한다**

`src/DBVC.Core/ScriptExporter.cs`:

```csharp
            result.IncludedCount = sections.Count;
            result.Script = sections.Count > 0
                ? ScriptGenerator.BuildScript(sections, kind, generatedAt, result.ExcludedObjects)
                : string.Empty;
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`

Expected: 전부 PASS. 기존 `BuildScript_WritesHeaderWithKindAndObjectCount`,
`BuildScript_IsDeterministicForTheSameInput`, `BuildScript_SkipsSectionsWithNoSql`이 계속 통과해야 한다
— 기본값 `null`이 기존 출력을 보존한다. 단언을 고친 `Export_Deployment_ExcludesObjectsWhoseFileIsMissing`도 통과해야 한다.

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Core/ScriptGenerator.cs src/DBVC.Core/ScriptExporter.cs tests/DBVC.Core.Tests/ScriptGeneratorTests.cs tests/DBVC.Core.Tests/ScriptExporterTests.cs
git commit -m "feat(core): 생성된 스크립트 헤더에 제외된 객체를 기록"
```

---

## Task 4: Vsix — 스크립트 생성 결과를 알림으로 전환

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:523-558` (`GenerateScript`)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: 기존 `IUserNotifier.ShowInfo(string title, string message)`, `ScriptExportResult.ExcludedObjects`·`IncludedCount`·`HasContent`
- Produces: 없음. `GenerateScript`는 계속 `private void`이다.

**배경.** `WarningMessage`는 지속 상태(매핑 안 됨, SMO 추출 실패) 배너이고 `Refresh`가 덮어쓴다.
스크립트 생성은 일회성 동작이므로 대화상자가 맞다. 성공했는데 제외가 없으면 지금은 아무 피드백도 없다.

제외 사유가 `ScriptKind`에 따라 다르다는 점에 주의한다 — Rollback은 되돌릴 이전 리비전이 없는 것이고,
Deployment는 작업 트리에 `.sql` 파일이 없는 것이다(`ScriptExporter.cs:39-47`).

- [ ] **Step 1: 깨지게 될 기존 테스트 두 개를 고친다**

`ViewChangesViewModelTests.cs:965`의 `GenerateRollbackScriptCommand_WarnsAndSkipsSave_WhenNoObjectHasAPreviousRevision`을 교체한다.

```csharp
        [Test]
        public void GenerateRollbackScriptCommand_NotifiesAndSkipsSave_WhenNoObjectHasAPreviousRevision()
        {
            var vm = NewViewModelWithOneCheckedChange(out _);
            _git.Setup(g => g.GetFileContentBeforeLastCommit(Server, Database, It.IsAny<string>()))
                .Returns((string?)null);

            vm.GenerateRollbackScriptCommand.Execute(null);

            Assert.That(_saveDialog.CallCount, Is.EqualTo(0), "저장할 내용이 없으면 대화상자를 띄우지 않아야 합니다");
            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("dbo.Users"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("이전 리비전이 없어"));
            Assert.That(vm.WarningMessage, Is.Null,
                "일회성 동작의 결과를 지속 상태 배너에 쓰면 안 됩니다");
            Assert.That(_notifier.Errors, Is.Empty, "내보낼 내용이 없는 것은 오류가 아닙니다");
        }
```

`:978`의 `GenerateDeploymentScriptCommand_ReportsExcludedObjectsAfterSaving`을 교체한다.

```csharp
        [Test]
        public void GenerateDeploymentScriptCommand_ReportsExcludedObjectsAfterSaving()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            // 파일이 없는 두 번째 객체를 목록에 추가한다.
            vm.Changes.Add(new ChangeItemViewModel
            {
                ObjectName = "dbo.Gone",
                State = "Modified",
                RelativePath = "dbo/Tables/Gone.sql",
                IsSelected = true
            });
            _saveDialog.PathToReturn = Path.Combine(repoPath, "deploy.sql");

            vm.GenerateDeploymentScriptCommand.Execute(null);

            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.InfoCalls[0].Title, Is.EqualTo("DBVC Deployment Script"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("1개 객체를 내보냈습니다"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("dbo.Gone"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("추출된 파일이 없어"),
                "Deployment의 제외 사유는 이전 리비전이 아니라 추출된 파일입니다");
            Assert.That(vm.WarningMessage, Is.Null);
        }
```

`RecordingNotifier`에 `InfoCalls`를 추가한다. `Infos`는 기존 테스트가 쓰므로 남겨 둔다.

```csharp
            /// <summary>ShowInfo에 실제로 전달된 (title, message) 쌍.</summary>
            public List<(string Title, string Message)> InfoCalls { get; } = new List<(string, string)>();
```

`ShowInfo` 본문을 고친다.

```csharp
            public void ShowInfo(string title, string message)
            {
                Infos.Add(message);
                InfoCalls.Add((title, message));
            }
```

- [ ] **Step 2: 새 테스트를 쓴다**

`GenerateDeploymentScriptCommand_ReportsExcludedObjectsAfterSaving` 다음에 추가한다.

```csharp
        [Test]
        public void GenerateDeploymentScriptCommand_NotifiesSuccess_EvenWhenNothingWasExcluded()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            _saveDialog.PathToReturn = Path.Combine(repoPath, "deploy.sql");

            vm.GenerateDeploymentScriptCommand.Execute(null);

            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1),
                "성공했는데 아무 피드백이 없으면 사용자는 저장됐는지 알 수 없습니다");
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("1개 객체를 내보냈습니다"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Not.Contain("제외"),
                "제외가 없으면 제외 문구를 붙이지 않습니다");
        }

        [Test]
        public void GenerateDeploymentScriptCommand_DoesNotNotify_WhenTheUserCancelsTheSaveDialog()
        {
            var vm = NewViewModelWithOneCheckedChange(out _);
            _saveDialog.PathToReturn = null;

            vm.GenerateDeploymentScriptCommand.Execute(null);

            Assert.That(_notifier.InfoCalls, Is.Empty, "취소는 오류도 아니고 완료도 아닙니다");
            Assert.That(_notifier.Errors, Is.Empty);
        }
```

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "GenerateDeploymentScriptCommand|GenerateRollbackScriptCommand"`

Expected: 위 4개 FAIL. `GenerateScript`가 아직 `WarningMessage`를 쓰고 `ShowInfo`를 부르지 않는다.

- [ ] **Step 4: `GenerateScript`를 고친다**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`의 `GenerateScript` 전체를 교체한다.

```csharp
        private void GenerateScript(ScriptKind kind)
        {
            if (!CanGenerateScript()) return;

            var kindLabel = kind == ScriptKind.Rollback ? "Rollback" : "Deployment";
            var title = $"DBVC {kindLabel} Script";

            var result = _scriptExporter.Export(
                ServerName!, DatabaseName!, GetSelectedRecords(), kind, DateTimeOffset.Now);

            if (!result.HasContent)
            {
                // 오류가 아니다. 내보낼 것이 없다는 사실을 알리고 끝낸다.
                _notifier.ShowInfo(title, WithExclusions("내보낼 내용이 없습니다.", result, kind));
                return;
            }

            var targetPath = _saveDialog.PromptForSavePath(
                $"{title} 저장",
                $"DBVC_{kindLabel}_{DatabaseName}.sql");

            // 사용자가 취소한 경우다. 오류가 아니다.
            if (string.IsNullOrWhiteSpace(targetPath)) return;

            try
            {
                File.WriteAllText(targetPath!, result.Script);
            }
            catch (Exception ex)
            {
                _notifier.ShowError($"{title} 저장 실패", ex.Message);
                return;
            }

            _notifier.ShowInfo(title, WithExclusions($"{result.IncludedCount}개 객체를 내보냈습니다.", result, kind));
        }

        /// <summary>
        /// 제외된 객체가 있으면 사유와 함께 덧붙인다.
        /// 사유가 <see cref="ScriptKind"/>에 따라 다르다 - Rollback은 되돌릴 이전 리비전이 없는 것이고,
        /// Deployment는 작업 트리에 추출된 .sql 파일이 없는 것이다.
        /// </summary>
        private static string WithExclusions(string message, ScriptExportResult result, ScriptKind kind)
        {
            if (result.ExcludedObjects.Count == 0) return message;

            var reason = kind == ScriptKind.Rollback ? "이전 리비전이 없어" : "추출된 파일이 없어";

            return message + Environment.NewLine +
                $"{result.ExcludedObjects.Count}개 객체는 {reason} 제외했습니다: " +
                string.Join(", ", result.ExcludedObjects);
        }
```

> `ScriptExportResult`는 `DBVC.Core` 네임스페이스에 있다. 파일 상단에 `using DBVC.Core;`가 이미 있는지 확인한다.

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`

Expected: 전부 PASS. 기존 `GenerateDeploymentScriptCommand_WritesTheMergedScriptToTheChosenPath`,
`GenerateDeploymentScriptCommand_DoesNothing_WhenTheUserCancelsTheSaveDialog`,
`GenerateDeploymentScriptCommand_Notifies_WhenWritingTheFileFails`도 계속 통과해야 한다.

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "fix(vsix): 스크립트 생성 결과를 경고 배너 대신 알림으로 표시"
```

---

## Task 5: 설계 문서·README 정합화 (P3 #8~#13)

**Files:**
- Modify: `docs/superpowers/specs/2026-07-31-dbvc-view-changes-design.md:27,48`
- Modify: `docs/superpowers/specs/2026-07-31-dbvc-ssms21-plugin-design.md:30,35`
- Modify: `docs/superpowers/specs/2026-08-01-dbvc-script-generation-design.md:33,59,66-70,85`
- Modify: `README.md:6`

**Interfaces:**
- Consumes: Task 3의 `BuildScript` 최종 시그니처, Task 4의 알림 방식
- Produces: 없음. 문서만 바뀐다.

이 태스크에는 테스트가 없다. 코드를 건드리지 않기 때문이다. 각 편집은 **현재 코드가 무엇을 하는지**를
확인한 뒤 문서를 그에 맞추는 작업이다. 반대 방향(문서에 맞춰 코드를 바꾸기)은 하지 않는다.

- [ ] **Step 1: view-changes 설계 두 곳 (P3 #8, #9)**

`docs/superpowers/specs/2026-07-31-dbvc-view-changes-design.md:27`

기존:

```markdown
  - Each item contains a CheckBox (for staging), an Icon (indicating M/A/D state), and the Object Name (e.g., `dbo.Users`).
```

교체:

```markdown
  - Each item contains a CheckBox (for staging), a State column showing `Modified` / `Added` / `Deleted` as text, and the Object Name (e.g., `dbo.Users`). Status icons are not implemented; the state is rendered as text.
```

`:48`의 문장 앞부분만 고친다. `"저장소 연결..."` 버튼 설명은 그대로 둔다.

기존:

```markdown
- If `ConfigManager` cannot resolve a mapping for the active database, a warning banner is shown above the content area ("Active Database is not mapped to a Git repository.") and commit actions are disabled.
```

교체:

```markdown
- The target database is entered manually in the Server / Database inputs and applied with the **Connect** button. There is no automatic "active database" detection — that would require Object Explorer integration, which is deferred for the same reason as Feature 10 (see [2026-08-01-dbvc-object-explorer-overlay.md](../plans/2026-08-01-dbvc-object-explorer-overlay.md)).
- If `ConfigManager` cannot resolve a mapping for the connected database, a warning banner is shown above the content area ("Active Database is not mapped to a Git repository.") and commit actions are disabled.
```

> 배너 안의 영문 문구 `"Active Database is not mapped to a Git repository."`는 코드에 실제로 있는
> 문자열이므로 바꾸지 않는다. 바꾸려면 코드도 함께 바꿔야 하는데 그건 이 작업의 범위가 아니다.
> `ViewChangesViewModel`의 `NotMappedWarning` 상수를 열어 실제 문구를 확인하고,
> 문서의 인용이 코드와 다르면 **코드 쪽 문구로 문서를 맞춘다.**

- [ ] **Step 2: ssms21 설계 두 곳 (P3 #10, #13)**

`docs/superpowers/specs/2026-07-31-dbvc-ssms21-plugin-design.md:30`

기존:

```markdown
  * `UiController`: 사용자 인터페이스(WPF/WinForms) 및 Object Explorer(OE) 컨텍스트 메뉴 제어.
```

교체:

```markdown
  * UI 계층: `UiController`라는 단일 클래스는 없다. `ViewChangesToolWindow`(창 등록), `ViewChangesControl`(WPF `UserControl`), `ViewChangesViewModel`과 `RelayCommand`(MVVM)로 나뉘어 있다. SQL 에디터 컨텍스트 메뉴는 `DBVC.Vsix/Commands`가 담당한다.
```

`:35`

기존:

```markdown
2. SSMS 플러그인의 `StateTracker`가 `DBVC_ChangeLog`를 주기적으로(또는 수동 새로고침 시) 읽어와 로컬 캐시를 업데이트.
```

교체:

```markdown
2. SSMS 플러그인의 `StateTracker`가 **사용자가 Refresh를 누를 때** `DBVC_ChangeLog`를 읽어와 로컬 캐시를 업데이트. 주기적 폴링은 구현되어 있지 않으며 계획에도 없다.
```

- [ ] **Step 3: script-generation 설계 네 곳 (P3 #11, #12 + 3.1·4절)**

`docs/superpowers/specs/2026-08-01-dbvc-script-generation-design.md:33`

기존:

```markdown
string BuildScript(IEnumerable<ScriptSection> sections, ScriptKind kind)
```

교체:

```markdown
string BuildScript(
    IEnumerable<ScriptSection>? sections,
    ScriptKind kind,
    DateTimeOffset generatedAt,
    IReadOnlyCollection<string>? excludedObjects = null)
```

바로 아래 불릿 목록에 한 줄을 더한다.

```markdown
* `generatedAt`을 인자로 받는 이유는 순수 함수를 유지하기 위해서다. 내부에서 `DateTimeOffset.Now`를 읽으면 출력이 매번 달라져 단위 테스트로 고정할 수 없다.
```

`:59`

기존:

```markdown
* 내용이 비어 있는 섹션은 건너뛰되, 건너뛴 사실을 헤더에 기록한다.
```

교체:

```markdown
* 내용이 비어 있는 섹션은 건너뛴다. 다만 실제 제외 판정은 `ScriptExporter`가 한다(3.2 참고) — `BuildScript`에 도달하는 섹션은 이미 걸러진 상태이므로 이 필터는 방어적이다.
* 제외된 객체가 있으면 헤더에 `Excluded: 2 (dbo.A, dbo.B)` 줄을 남긴다. 목록은 `excludedObjects` 인자로 전달받는다. 제외가 없으면 이 줄을 넣지 않는다. 알림 대화상자는 닫으면 사라지지만 헤더는 파일과 함께 남는다.
```

`:66-70`의 **Rollback의 "이전 리비전" 정의** 문단 끝에 한 문단을 더한다.

```markdown
**이미 삭제된 객체.** HEAD에 없는 경로에 대해 `repo.Commits.QueryBy(path)`는 빈 결과를 준다.
이 경우 커밋을 최신순으로 거슬러 파일이 **마지막으로 존재했던 시점의 내용**을 쓴다
(`GitManager.GetFileContentBeforeLastCommit`). DROP된 객체야말로 롤백의 주요 대상이므로
"이전 리비전이 없다"고 판정해 제외하면 안 된다.
```

`:85`

기존:

```markdown
* 생성 결과를 요약해 알린다. 예: `3개 객체를 내보냈습니다. 2개 객체는 이전 리비전이 없어 제외했습니다.`
```

교체:

```markdown
* 생성 결과를 `IUserNotifier.ShowInfo`로 알린다. 지속 상태 배너(`WarningMessage`)는 매핑 누락·추출 실패 전용이며 `Refresh`가 덮어쓰므로 일회성 동작의 결과를 담지 않는다.
* 제외 사유는 `ScriptKind`에 따라 다르다. Rollback은 `3개 객체를 내보냈습니다. / 2개 객체는 이전 리비전이 없어 제외했습니다: dbo.A, dbo.B`, Deployment는 같은 형식에 `추출된 파일이 없어`를 쓴다.
```

- [ ] **Step 4: README 한 곳 (P3 #13)**

`README.md:6`

기존:

```markdown
- **변경 사항 자동 감지 (DDL Trigger):** 데이터베이스에서 발생하는 스키마 변경 사항을 실시간으로 감지하고 추적합니다. (`DBVC_ChangeLog` 테이블 활용)
```

교체:

```markdown
- **변경 사항 자동 감지 (DDL Trigger):** DDL 트리거가 스키마 변경을 발생 즉시 `DBVC_ChangeLog` 테이블에 기록합니다. 기록된 변경은 **Refresh를 누를 때** 화면에 반영됩니다(주기적 폴링은 하지 않습니다).
```

- [ ] **Step 5: 문서와 코드가 실제로 맞는지 확인한다**

각 편집이 사실인지 코드로 확인한다. 하나라도 어긋나면 문서가 아니라 **확인 내용**을 고친다.

```bash
# #9: State가 텍스트 컬럼인지
grep -n "State" src/DBVC.Vsix/UI/ViewChangesControl.xaml

# #8: 배너 문구 상수의 실제 값
grep -n "NotMappedWarning" src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs

# #10: UiController가 정말 없는지, 실제 UI 타입이 무엇인지
grep -rn "UiController" src/ ; ls src/DBVC.Vsix/UI src/DBVC.Vsix/ViewModels src/DBVC.Vsix/Commands

# #13: 폴링이 정말 없는지 (Timer/DispatcherTimer가 없어야 한다)
grep -rn "Timer" src/DBVC.Vsix src/DBVC.Core --include=*.cs | grep -v obj/

# #11, #12: 최종 시그니처와 삭제된 객체 처리
grep -n "BuildScript" -A 6 src/DBVC.Core/ScriptGenerator.cs
grep -n "이미 삭제된 객체" -A 4 src/DBVC.Core/GitManager.cs
```

- [ ] **Step 6: 전체 테스트를 돌려 문서 편집이 아무것도 깨지 않았는지 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net10.0`

Expected: 전부 PASS.

- [ ] **Step 7: 커밋**

```bash
git add docs/superpowers/specs/2026-07-31-dbvc-view-changes-design.md docs/superpowers/specs/2026-07-31-dbvc-ssms21-plugin-design.md docs/superpowers/specs/2026-08-01-dbvc-script-generation-design.md README.md
git commit -m "docs: 설계 문서와 README를 실제 구현에 맞게 정정 (P3 #8~#13)"
```

---

## 수동 검증 체크리스트 (SSMS 21)

CI가 검증하지 못하는 항목이다. 실제 SSMS 21에서 확인한다.

- [ ] **인증이 필요한 원격에서 Pull.** 사내 Azure DevOps 등 Windows 통합 인증 원격에 대해 Pull이 **성공**하는지. 이번 변경의 핵심 목적이며, 지금까지는 항상 실패하던 경로다.
- [ ] **자격 증명을 요구하는 원격에서 Pull.** GitHub HTTPS처럼 사용자명/토큰을 요구하는 원격에서 "원격이 사용자 자격 증명을 요구합니다"라는 한국어 안내가 뜨는지. libgit2 영문 원문이 보이면 실패다.
- [ ] **겹치는 미커밋 변경으로 Pull.** 원격이 바꾼 파일을 로컬에서 커밋하지 않고 수정한 뒤 Pull → 확인 → "저장소는 변경되지 않았습니다" 안내가 뜨고, **로컬 수정 내용이 그대로 남아 있는지.**
- [ ] **확인 대화상자 문구.** 미커밋 변경이 있을 때 뜨는 확인 창에 "거부됩니다. 이 경우 저장소는 그대로입니다"와 "사라질 수 있습니다"가 모두 보이는지. 창이 SSMS 뒤로 숨지 않는지.
- [ ] **Deployment Script 성공 알림.** 제외 없이 성공했을 때 "N개 객체를 내보냈습니다" 알림이 뜨는지. 이전에는 아무 피드백이 없었다.
- [ ] **제외 문구가 kind별로 맞는지.** 작업 트리에 파일이 없는 객체를 포함해 Deployment를 만들면 "추출된 파일이 없어", 이력이 하나뿐인 객체로 Rollback을 만들면 "이전 리비전이 없어"가 나오는지.
- [ ] **생성된 `.sql` 헤더.** 제외가 있었던 스크립트를 텍스트 편집기로 열어 `Excluded: N (...)` 줄이 있는지.
- [ ] **경고 배너가 오염되지 않는지.** 스크립트를 생성한 뒤 상단 경고 배너에 생성 결과가 남지 않는지. 매핑 경고만 표시되어야 한다.
