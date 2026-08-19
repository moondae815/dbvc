# Push 버튼 활성화 로직 개선 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 로컬 저장소에 원격으로 보낼 커밋이 있을 때만 Push 버튼을 활성화한다.

**Architecture:** `IGitManager`에 `HasCommitsToPush` 메서드를 추가하여 로컬 저장소의 원격 추적 상태를 파악하고, `ViewChangesViewModel`의 `CanPush` 조건에서 이 메서드를 사용하도록 수정한다. 

**Tech Stack:** C#, NUnit, Moq, LibGit2Sharp, WPF/MVVM (ViewModel)

**Spec:** docs/superpowers/specs/2026-08-19-push-button-activation-design.md

## Global Constraints

- 한국어 주석과 "왜"를 설명하는 문체 사용
- 커밋 메시지는 한국어 명령형 현재시제 (`feat(core): ...`)
- TDD 준수 (실패하는 테스트 작성 -> 통과하도록 구현)
- `Microsoft.Data.SqlClient` 및 `Microsoft.SqlServer.SqlManagementObjects` 패키지 버전 고정 유지

---

### Task 1: `IGitManager` 확장 및 `GitManager` 구현

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Core/GitManager.cs`
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Produces: `bool HasCommitsToPush(string serverName, string databaseName)` in `IGitManager`

- [ ] **Step 1: Write the failing test**

`tests/DBVC.Core.Tests/GitManagerTests.cs` 파일에 `PushChanges_` 테스트 근처에 다음 테스트를 추가합니다.

```csharp
        [Test]
        public void HasCommitsToPush_ReturnsTrue_WhenLocalIsAheadOfRemote()
        {
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            var git = NewGitManager("ServerA", "DB1", localPath);

            // 클론 직후에는 앞선 커밋이 없다
            Assert.That(git.HasCommitsToPush("ServerA", "DB1"), Is.False);

            // 새 커밋을 만들면 앞선다
            CommitOneFile(localPath, "test.sql", "select 1", "new commit");
            Assert.That(git.HasCommitsToPush("ServerA", "DB1"), Is.True);
        }

        [Test]
        public void HasCommitsToPush_ReturnsFalse_WhenNoRemoteOrMapping()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("ServerA", "DB1", repoPath);

            // 매핑은 있지만 원격이 없으므로 false
            Assert.That(git.HasCommitsToPush("ServerA", "DB1"), Is.False);
            
            // 매핑이 없으면 false
            Assert.That(git.HasCommitsToPush("ServerB", "DB2"), Is.False);
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~HasCommitsToPush"`
Expected: FAIL (Cannot resolve symbol 'HasCommitsToPush')

- [ ] **Step 3: Write minimal implementation**

`src/DBVC.Core/Abstractions.cs`의 `IGitManager`에 인터페이스 추가:
```csharp
        bool HasCommitsToPush(string serverName, string databaseName);
```

`src/DBVC.Core/GitManager.cs`의 `PushChanges` 근처에 메서드 구현 추가:
```csharp
        public bool HasCommitsToPush(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return false;

            try
            {
                using var repo = new Repository(repoPath);
                
                // 원격이 없거나 추적 중인 브랜치가 없으면 올릴 수 없다(libgit2 예외와 일치).
                if (!repo.Network.Remotes.Any() || !repo.Head.IsTracking) return false;
                
                return repo.Head.TrackingDetails.AheadBy > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.HasCommitsToPush failed for '{repoPath}': {ex.Message}");
                return false;
            }
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Core.Tests --filter "FullyQualifiedName~HasCommitsToPush"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): HasCommitsToPush 메서드 추가"
```

---

### Task 2: `ViewChangesViewModel` 조건 연동

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `_gitManager.HasCommitsToPush(...)`

- [ ] **Step 1: Write the failing test**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`에 `Push` 명령의 `CanExecute`를 검증하는 테스트 추가:

```csharp
        [Test]
        public void PushCommand_CanExecute_OnlyWhenHasCommitsToPushIsTrue()
        {
            var vm = NewConnectedViewModel(); // HasContext = true, IsMapped = true (기본 설정)
            
            // 처음에는 앞선 커밋이 없다고 가정
            _git.Setup(g => g.HasCommitsToPush(Server, Database)).Returns(false);
            vm.ConnectCommand.Execute(null); // Context Probe 다시 실행 및 Command 갱신 대기용
            // 테스트 헬퍼: 비동기 작업이 끝날 때까지 대기
            Thread.Sleep(50); // Background scheduler 
            
            // CanExecute 갱신 트리거를 위해 수동으로 불리는 RaiseActionCanExecuteChanged 흉내 (혹은 뷰모델 내부 동작 의존)
            // 가장 확실한 것은 명시적 갱신을 부르거나 Mock 반환값을 바꾸고 상태를 재판정시키는 것.
            
            // NewConnectedViewModel 호출 시 ApplyContext -> ApplyContextProbe가 돌고 RaiseActionCanExecuteChanged가 호출됨.
            Assert.That(vm.PushCommand.CanExecute(null), Is.False, "커밋이 없으므로 비활성화되어야 함");

            _git.Setup(g => g.HasCommitsToPush(Server, Database)).Returns(true);
            
            // ViewChangesViewModel은 상태 변경 시 CanExecute를 갱신하므로 이 테스트에서는 Refresh를 호출해 갱신을 유도.
            vm.RefreshCommand.Execute(null);
            Thread.Sleep(100);
            
            Assert.That(vm.PushCommand.CanExecute(null), Is.True, "앞선 커밋이 생기면 활성화되어야 함");
        }
```

*참고: 위 테스트 코드는 현재 코드베이스의 비동기 헬퍼 동작을 고려하여 다듬어 적용해야 할 수도 있습니다.* 보다 안정적인 테스트를 위해 `vm.PushCommand.CanExecute(null)` 상태를 확인합니다.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~PushCommand_CanExecute_OnlyWhenHasCommitsToPushIsTrue"`
Expected: FAIL (기존 로직은 HasCommitsToPush를 보지 않고 항상 True를 반환하므로 첫 번째 Assert에서 실패)

- [ ] **Step 3: Write minimal implementation**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`의 `CanPush` 메서드를 수정:

```csharp
        private bool CanPush() => HasContext && IsMapped && _gitManager.HasCommitsToPush(ServerName!, DatabaseName!);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~PushCommand_CanExecute_OnlyWhenHasCommitsToPushIsTrue"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): Push 버튼이 보낼 커밋이 있을 때만 활성화되도록 수정"
```
