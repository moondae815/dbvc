# 브랜치 조작 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 지라 티켓 한 바퀴(브랜치 만들기 → 커밋 → 첫 Push → 병합 뒤 돌아오기)를 SSMS 밖으로 나가지 않고 돌 수 있게 한다.

**Architecture:** `IGitManager`에 `GetBranches`·`CreateBranch`·`SwitchBranch`를 더하고, `PushChanges`에 `setUpstream` 갈래를 낸다. 전환은 작업 트리가 깨끗할 때만 하며 그 판정은 Core가 한다. 두 브랜치 동작은 `MappingPolicy`에서 `Write` 모드로 한정한다. 화면은 기존 `IUserNotifier.Confirm`과 배경 스케줄러 왕복 패턴을 그대로 쓴다.

**Tech Stack:** C# / .NET Standard 2.0 + .NET Framework 4.8, LibGit2Sharp, WPF(MVVM), NUnit 4, Moq

**Spec:** [`docs/superpowers/specs/2026-09-10-dbvc-branch-operations-design.md`](../specs/2026-09-10-dbvc-branch-operations-design.md)

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip 포함. libgit2/서버의 영문 원문은 인용할 때만 그대로 싣는다.
- **주석은 "왜"만 적는다.** 한국어 평서문, 기존 문체(함정과 근거를 남긴다)를 따른다.
- **테스트 이름은 영어 `Method_Result_WhenCondition`.**
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프.** 예: `feat(core): 브랜치 전환을 더한다`
- **TDD:** 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `SqlManagementObjects 171.30.0` 고정.
- **테스트 프로젝트에 MDS/SMO를 직접 PackageReference 하지 않는다.** 전이 참조로만 받는다.
- **`MappingPolicy.IsAllowed`의 `default`는 예외를 던진다.** 새 `DbvcOperation`을 더하면 표를 반드시 고쳐야 한다 — 그 성질을 일부러 쓴다.
- 빌드·테스트 명령:
  - `dotnet build DBVC.slnx`
  - `dotnet test tests/DBVC.Core.Tests -f net10.0`
  - `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
  - 단일 테스트: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~<이름>"`

## 파일 구조

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Core/Models/BranchInfo.cs` (신규) | 브랜치 한 줄의 표시용 자료 |
| `src/DBVC.Core/Models/BranchResult.cs` (신규) | 브랜치 조작의 성패와 거부 사유 |
| `src/DBVC.Core/Models/PushResult.cs` | `NoUpstream` 추가 |
| `src/DBVC.Core/MappingPolicy.cs` | `DbvcOperation` 둘 추가 + 표 |
| `src/DBVC.Core/Abstractions.cs` | `IGitManager` 표면 |
| `src/DBVC.Core/GitManager.cs` | 세 메서드 + `PushChanges` 갈래 |
| `src/DBVC.Vsix/Services/IBranchDialog.cs` (신규) | 브랜치 이름 입력·선택 대화상자의 이음매 |
| `src/DBVC.Vsix/UI/BranchDialog.xaml(.cs)` (신규) | 그 구현 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 명령 둘 + Push 왕복 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 버튼 둘 |
| `tests/DBVC.Core.Tests/MappingPolicyTests.cs` | 표 검증 |
| `tests/DBVC.Core.Tests/GitManagerTests.cs` | 임시 저장소로 동작 검증 |
| `tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs` | `RecordingBranchDialog` |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | 명령 게이트와 왕복 |

---

### Task 1: 정책 표에 브랜치 동작을 올린다

`MappingPolicy`는 순수 함수라 DB·Git 없이 검증된다. 나머지 모든 작업이 이 판정에 기대므로 먼저 세운다.

**Files:**
- Modify: `src/DBVC.Core/MappingPolicy.cs`
- Test: `tests/DBVC.Core.Tests/MappingPolicyTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `DbvcOperation.CreateBranch`, `DbvcOperation.SwitchBranch`. `MappingPolicy.IsAllowed(mode, op)`가 이 둘에 대해 `mode == MappingMode.Write`를 돌려준다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/MappingPolicyTests.cs`의 기존 `[TestCase]` 목록에 줄을 더한다.

```csharp
        [TestCase(DbvcOperation.CreateBranch)]
        [TestCase(DbvcOperation.SwitchBranch)]
        public void IsAllowed_ReturnsTrue_WhenModeIsWriteAndOperationIsBranch(DbvcOperation operation)
        {
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Write, operation), Is.True);
        }

        [TestCase(MappingMode.Deploy, DbvcOperation.CreateBranch)]
        [TestCase(MappingMode.Deploy, DbvcOperation.SwitchBranch)]
        [TestCase(MappingMode.Audit, DbvcOperation.CreateBranch)]
        [TestCase(MappingMode.Audit, DbvcOperation.SwitchBranch)]
        public void IsAllowed_ReturnsFalse_WhenBranchOperationOnPinnedClone(MappingMode mode, DbvcOperation operation)
        {
            // 배포·감사 클론은 고정 브랜치가 필수다. 옮기는 순간 비교 기준이 무너진다.
            Assert.That(MappingPolicy.IsAllowed(mode, operation), Is.False);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicy"`
Expected: 컴파일 실패 — `DbvcOperation`에 `CreateBranch`·`SwitchBranch`가 없다.

- [ ] **Step 3: 열거형에 둘을 더한다**

`src/DBVC.Core/MappingPolicy.cs`의 `enum DbvcOperation`에서 `Discard` 아래에 넣는다.

```csharp
        /// <summary>HEAD에서 새 브랜치를 만들고 체크아웃한다.</summary>
        CreateBranch,

        /// <summary>다른 브랜치로 갈아탄다.</summary>
        SwitchBranch,
```

- [ ] **Step 4: 다시 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicy"`
Expected: FAIL — `IsAllowed`의 `default`가 `InvalidOperationException: 처리되지 않은 DbvcOperation: CreateBranch`를 던진다. **이 예외가 나오는 것이 표를 고치라는 신호다.**

- [ ] **Step 5: 표를 고친다**

`IsAllowed`의 `switch`에 `case`를 더한다. `Compare` 앞에 둔다.

```csharp
                case DbvcOperation.CreateBranch:
                case DbvcOperation.SwitchBranch:
                    // 배포·감사 클론은 고정 브랜치가 필수다(MappingConfig.Branch). 브랜치를 옮기는
                    // 순간 비교 기준이 무너지고 RepositoryStateEvaluator가 BranchMismatch로 화면을
                    // 덮는다. 허용해도 곧바로 막히는 동작이라 표에서 먼저 끊는다.
                    return mode == MappingMode.Write;
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicy"`
Expected: PASS

- [ ] **Step 7: 이름 표를 채운다**

`GetOperationName`에도 두 동작을 더한다. 빠뜨리면 거부 문구가 빈칸이 된다.

```csharp
                case DbvcOperation.CreateBranch: return "브랜치 만들기";
                case DbvcOperation.SwitchBranch: return "브랜치 전환";
```

- [ ] **Step 8: 커밋한다**

```bash
git add src/DBVC.Core/MappingPolicy.cs tests/DBVC.Core.Tests/MappingPolicyTests.cs
git commit -m "feat(core): 브랜치 동작 둘을 정책 표에 올린다"
```

---

### Task 2: 브랜치 목록을 낸다

**Files:**
- Create: `src/DBVC.Core/Models/BranchInfo.cs`
- Modify: `src/DBVC.Core/Abstractions.cs`, `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: Task 1의 `DbvcOperation`은 쓰지 않는다 — 목록 조회는 읽기라 모드로 막지 않는다.
- Produces:
  - `class BranchInfo { string Name; bool IsCurrent; bool IsRemoteOnly; }`
  - `IReadOnlyList<BranchInfo> IGitManager.GetBranches(string serverName, string databaseName)` — 매핑이 없으면 빈 목록.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs`에 더한다. 헬퍼 `NewRepoWithCommit()`과 `NewGitManager()`는 이미 있다.

```csharp
        // ---------- GetBranches ----------

        [Test]
        public void GetBranches_MarksCurrent_WhenRepositoryHasSeveralBranches()
        {
            var path = NewRepoWithCommit();
            using (var repo = new Repository(path))
            {
                repo.CreateBranch("PROJ-123");
            }
            var git = NewGitManager("localhost", "testdb", path);

            var branches = git.GetBranches("localhost", "testdb");

            var current = branches.Single(b => b.IsCurrent);
            Assert.That(branches.Select(b => b.Name), Does.Contain("PROJ-123"));
            Assert.That(current.Name, Is.Not.EqualTo("PROJ-123"),
                "CreateBranch는 체크아웃하지 않으므로 HEAD는 그대로여야 합니다");
        }

        [Test]
        public void GetBranches_ReturnsEmpty_WhenDatabaseIsNotMapped()
        {
            var git = NewGitManager("localhost", "other", NewRepoWithCommit());

            Assert.That(git.GetBranches("localhost", "testdb"), Is.Empty);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GetBranches"`
Expected: 컴파일 실패 — `GetBranches`가 없다.

- [ ] **Step 3: 모델을 만든다**

`src/DBVC.Core/Models/BranchInfo.cs`

```csharp
namespace DBVC.Core.Models
{
    /// <summary>브랜치 목록 한 줄. 화면이 고르게 하는 데 필요한 만큼만 담는다.</summary>
    public class BranchInfo
    {
        /// <summary>사람이 읽는 이름. 원격 전용이면 remote 접두사를 뗀 값이다(origin/PROJ-1 → PROJ-1).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>지금 체크아웃되어 있는 브랜치인가.</summary>
        public bool IsCurrent { get; set; }

        /// <summary>
        /// 로컬에는 없고 원격 추적 참조로만 있는가. 고르면 로컬 브랜치를 만들어 붙는다.
        /// 남이 만든 티켓 브랜치에 합류하는 경로다.
        /// </summary>
        public bool IsRemoteOnly { get; set; }
    }
}
```

- [ ] **Step 4: 인터페이스에 더한다**

`src/DBVC.Core/Abstractions.cs`의 `IGitManager`에서 `PushChanges` 위에 넣는다.

```csharp
        /// <summary>
        /// 로컬 브랜치와 원격 전용 브랜치를 한 벌로 낸다. 매핑이 없으면 빈 목록이다.
        /// 마지막 fetch 기준의 로컬 값이며 네트워크를 쓰지 않는다 - '원격 확인'과 같은 규칙이다.
        /// </summary>
        IReadOnlyList<BranchInfo> GetBranches(string serverName, string databaseName);
```

- [ ] **Step 5: 구현한다**

`src/DBVC.Core/GitManager.cs`에 더한다.

```csharp
        public IReadOnlyList<BranchInfo> GetBranches(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return Array.Empty<BranchInfo>();

            using var repo = new Repository(repoPath);

            var locals = repo.Branches
                .Where(b => !b.IsRemote)
                .Select(b => new BranchInfo
                {
                    Name = b.FriendlyName,
                    IsCurrent = b.IsCurrentRepositoryHead,
                    IsRemoteOnly = false
                })
                .ToList();

            var localNames = new HashSet<string>(locals.Select(b => b.Name), StringComparer.Ordinal);

            // origin/HEAD는 실제 브랜치가 아니라 기본 브랜치를 가리키는 심볼릭 참조다.
            // 목록에 넣으면 고를 수 없는 항목이 하나 생긴다.
            var remotes = repo.Branches
                .Where(b => b.IsRemote)
                .Select(b => b.FriendlyName)
                .Select(StripRemotePrefix)
                .Where(name => name != null && name != "HEAD" && !localNames.Contains(name!))
                .Distinct(StringComparer.Ordinal)
                .Select(name => new BranchInfo { Name = name!, IsCurrent = false, IsRemoteOnly = true });

            return locals.Concat(remotes).OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>"origin/PROJ-1" → "PROJ-1". 슬래시가 없으면 null이다.</summary>
        private static string? StripRemotePrefix(string friendlyName)
        {
            var slash = friendlyName.IndexOf('/');
            return slash < 0 ? null : friendlyName.Substring(slash + 1);
        }
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GetBranches"`
Expected: PASS

- [ ] **Step 7: 커밋한다**

```bash
git add src/DBVC.Core/Models/BranchInfo.cs src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 브랜치 목록을 낸다"
```

---

### Task 3: HEAD에서 브랜치를 만든다

**Files:**
- Create: `src/DBVC.Core/Models/BranchResult.cs`
- Modify: `src/DBVC.Core/Abstractions.cs`, `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `DbvcOperation.CreateBranch` (Task 1)
- Produces:
  - `class BranchResult { bool Succeeded; string? Message; IReadOnlyList<string> BlockingPaths; }`
  - `BranchResult IGitManager.CreateBranch(string serverName, string databaseName, string branchName)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        // ---------- CreateBranch ----------

        [Test]
        public void CreateBranch_KeepsWorkingTree_WhenTreeIsDirty()
        {
            // HEAD에서 만드는 것은 같은 커밋에 이름표를 붙이는 일이라 트리를 건드리지 않는다.
            var path = NewRepoWithCommit();
            WriteRepoFile(path, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            var git = NewGitManager("localhost", "testdb", path);

            var result = git.CreateBranch("localhost", "testdb", "PROJ-123");

            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(File.Exists(Path.Combine(path, "dbo", "Tables", "Orders.sql")), Is.True,
                "미커밋 파일이 그대로 있어야 합니다");
            using var repo = new Repository(path);
            Assert.That(repo.Head.FriendlyName, Is.EqualTo("PROJ-123"));
        }

        [Test]
        public void CreateBranch_Refuses_WhenBranchAlreadyExists()
        {
            var path = NewRepoWithCommit();
            using (var repo = new Repository(path)) repo.CreateBranch("PROJ-123");
            var git = NewGitManager("localhost", "testdb", path);

            var result = git.CreateBranch("localhost", "testdb", "PROJ-123");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Message, Does.Contain("이미"));
        }

        [Test]
        public void CreateBranch_Throws_WhenModeIsAudit()
        {
            // PushChanges_Throws_WhenModeIsAudit이 쓰는 헬퍼와 같은 것이다.
            // 서버·DB 이름은 그 헬퍼가 정하므로 상수 Server/Database를 쓴다.
            NewRepositoryWithCommit(out _, out var git, MappingMode.Audit);

            Assert.Throws<OperationNotAllowedException>(
                () => git.CreateBranch(Server, Database, "PROJ-123"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CreateBranch"`
Expected: 컴파일 실패 — `CreateBranch`가 없다.

- [ ] **Step 3: 결과 모델을 만든다**

`src/DBVC.Core/Models/BranchResult.cs`

```csharp
using System;
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    /// <summary>
    /// 브랜치 조작의 결과. 실패를 예외로 던지지 않는 이유는 거부가 예외적 사건이 아니기
    /// 때문이다 - 더러운 트리에서 전환을 누르는 것은 정상적인 사용자 행동이고, 화면은
    /// 무엇이 막았는지를 목록으로 보여 줘야 한다.
    /// </summary>
    public class BranchResult
    {
        public bool Succeeded { get; set; }

        /// <summary>실패 사유. 성공이면 null이다. 한국어이며 화면이 그대로 띄운다.</summary>
        public string? Message { get; set; }

        /// <summary>전환을 막은 미커밋 파일들. 그 외의 경우에는 빈 목록이다.</summary>
        public IReadOnlyList<string> BlockingPaths { get; set; } = Array.Empty<string>();

        public static BranchResult Ok() => new BranchResult { Succeeded = true };

        public static BranchResult Fail(string message, IReadOnlyList<string>? blocking = null) =>
            new BranchResult
            {
                Succeeded = false,
                Message = message,
                BlockingPaths = blocking ?? Array.Empty<string>()
            };
    }
}
```

- [ ] **Step 4: 인터페이스와 구현을 더한다**

`Abstractions.cs`의 `IGitManager`:

```csharp
        /// <summary>
        /// HEAD에서 브랜치를 만들고 체크아웃한다. 작업 트리는 바뀌지 않으므로
        /// 미커밋 변경이 있어도 안전하다.
        /// </summary>
        BranchResult CreateBranch(string serverName, string databaseName, string branchName);
```

`GitManager.cs`:

```csharp
        public BranchResult CreateBranch(string serverName, string databaseName, string branchName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return BranchResult.Fail("이 데이터베이스에 연결된 저장소가 없습니다.");

            EnsureAllowed(serverName, databaseName, DbvcOperation.CreateBranch);

            if (string.IsNullOrWhiteSpace(branchName))
                return BranchResult.Fail("브랜치 이름을 입력하세요.");

            using var repo = new Repository(repoPath);

            if (repo.Branches[branchName] != null)
                return BranchResult.Fail($"'{branchName}' 브랜치가 이미 있습니다. 다른 이름을 쓰거나 전환하세요.");

            try
            {
                var created = repo.CreateBranch(branchName);
                Commands.Checkout(repo, created);
            }
            catch (LibGit2SharpException ex)
            {
                // 이름 규칙 위반(공백, .. 등)은 libgit2가 영문으로 거부한다. 원문을 함께 싣는다 -
                // 무엇이 잘못된 글자인지는 그쪽이 더 정확히 말한다.
                return BranchResult.Fail($"'{branchName}' 브랜치를 만들지 못했습니다. {ex.Message}");
            }

            return BranchResult.Ok();
        }
```

`EnsureAllowed`는 아직 없다. `PushChanges`가 인라인으로 하던 검사를 그대로 뽑아 `GitManager`에 private으로 둔다 — 브랜치 둘이 같은 검사를 쓰므로 세 번째 복사가 생기기 전에 만든다.

```csharp
        /// <summary>
        /// 매핑의 mode가 이 연산을 허용하지 않으면 던진다. 화면의 CanExecute와 같은 함수를
        /// 쓰는 것이 규약이다 - 판정이 두 곳에 생기면 언젠가 갈라진다.
        /// </summary>
        private void EnsureAllowed(string serverName, string databaseName, DbvcOperation operation)
        {
            var mapping = _configManager?.TryGetMapping(serverName, databaseName);
            if (mapping != null && !MappingPolicy.IsAllowed(mapping.Mode, operation))
            {
                throw new OperationNotAllowedException(mapping.Mode, operation);
            }
        }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~CreateBranch"`
Expected: PASS (3건)

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Core/Models/BranchResult.cs src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): HEAD에서 브랜치를 만든다"
```

---

### Task 4: 깨끗할 때만 전환한다

이 작업의 핵심은 기능이 아니라 **거부**다. 거부가 정확해야 원래 막으려던 사고가 막힌다.

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs`, `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `BranchResult` (Task 3), `DbvcOperation.SwitchBranch` (Task 1), `BranchInfo` (Task 2)
- Produces: `BranchResult IGitManager.SwitchBranch(string serverName, string databaseName, string branchName)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        // ---------- SwitchBranch ----------

        [Test]
        public void SwitchBranch_Switches_WhenTreeIsClean()
        {
            var path = NewRepoWithCommit();
            string original;
            using (var repo = new Repository(path))
            {
                repo.CreateBranch("PROJ-123");
                original = repo.Head.FriendlyName;
            }
            var git = NewGitManager("localhost", "testdb", path);

            var result = git.SwitchBranch("localhost", "testdb", "PROJ-123");

            Assert.That(result.Succeeded, Is.True, result.Message);
            using var after = new Repository(path);
            Assert.That(after.Head.FriendlyName, Is.EqualTo("PROJ-123"));
            Assert.That(original, Is.Not.EqualTo("PROJ-123"));
        }

        [Test]
        public void SwitchBranch_Refuses_WhenTreeIsDirty()
        {
            var path = NewRepoWithCommit();
            using (var repo = new Repository(path)) repo.CreateBranch("PROJ-123");
            WriteRepoFile(path, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            var git = NewGitManager("localhost", "testdb", path);

            var result = git.SwitchBranch("localhost", "testdb", "PROJ-123");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.BlockingPaths, Does.Contain("dbo/Tables/Orders.sql"),
                "무엇이 막았는지 파일 이름으로 말해야 합니다");
            using var after = new Repository(path);
            Assert.That(after.Head.FriendlyName, Is.Not.EqualTo("PROJ-123"),
                "거부했으면 브랜치도 그대로여야 합니다");
        }

        [Test]
        public void SwitchBranch_Refuses_WhenBranchDoesNotExist()
        {
            var git = NewGitManager("localhost", "testdb", NewRepoWithCommit());

            var result = git.SwitchBranch("localhost", "testdb", "PROJ-999");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Message, Does.Contain("PROJ-999"));
        }

        [Test]
        public void SwitchBranch_CreatesLocalBranch_WhenBranchExistsOnlyOnRemote()
        {
            // 남이 만든 티켓 브랜치에 합류하는 경로다. git checkout <name>과 같은 동작이다.
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            using (var origin = new Repository(originPath))
            {
                // bare 저장소에 브랜치를 하나 더 만든다.
                origin.CreateBranch("PROJ-123", origin.Head.Tip);
            }
            using (var local = new Repository(localPath))
            {
                Commands.Fetch(local, "origin", Array.Empty<string>(), null, null);
            }
            var git = NewGitManager("localhost", "testdb", localPath);

            var result = git.SwitchBranch("localhost", "testdb", "PROJ-123");

            Assert.That(result.Succeeded, Is.True, result.Message);
            using var after = new Repository(localPath);
            Assert.That(after.Head.FriendlyName, Is.EqualTo("PROJ-123"));
            Assert.That(after.Head.IsTracking, Is.True, "원격을 추적하도록 붙여야 합니다");
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SwitchBranch"`
Expected: 컴파일 실패 — `SwitchBranch`가 없다.

- [ ] **Step 3: 인터페이스에 더한다**

```csharp
        /// <summary>
        /// 브랜치를 갈아탄다. 미커밋 변경이 하나라도 있으면 갈아타지 않고 그 목록을 담아 돌려준다.
        /// 로컬에 없고 원격에만 있는 이름이면 그것을 추적하는 로컬 브랜치를 만들어 붙는다.
        /// </summary>
        BranchResult SwitchBranch(string serverName, string databaseName, string branchName);
```

- [ ] **Step 4: 구현한다**

```csharp
        public BranchResult SwitchBranch(string serverName, string databaseName, string branchName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return BranchResult.Fail("이 데이터베이스에 연결된 저장소가 없습니다.");

            EnsureAllowed(serverName, databaseName, DbvcOperation.SwitchBranch);

            using var repo = new Repository(repoPath);

            // libgit2의 CheckoutConflictException에 기대지 않는다 - 그것은 대상 브랜치와 겹치는
            // 파일이 있을 때만 난다. 겹치지 않는 미커밋 변경은 조용히 딸려가 다음 브랜치의
            // 커밋에 섞이는데, 그것이 애초에 이 기능을 도구에 넣지 않으려던 사고다.
            var blocking = repo.RetrieveStatus()
                .Where(e => e.State != FileStatus.Ignored && e.State != FileStatus.Unaltered)
                .Select(e => e.FilePath.Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            if (blocking.Count > 0)
            {
                return BranchResult.Fail(
                    "커밋되지 않은 변경이 있어 브랜치를 바꿀 수 없습니다. " +
                    "먼저 커밋하거나 되돌린 뒤 다시 시도하세요.",
                    blocking);
            }

            var target = repo.Branches[branchName];

            if (target == null)
            {
                // 원격에만 있는 이름이면 그것을 추적하는 로컬 브랜치를 만든다.
                var remote = repo.Branches
                    .FirstOrDefault(b => b.IsRemote && StripRemotePrefix(b.FriendlyName) == branchName);

                if (remote == null)
                    return BranchResult.Fail($"'{branchName}' 브랜치를 찾을 수 없습니다.");

                var created = repo.CreateBranch(branchName, remote.Tip);
                target = repo.Branches.Update(created, b => b.TrackedBranch = remote.CanonicalName);
            }

            Commands.Checkout(repo, target);
            return BranchResult.Ok();
        }
```

> **Task 5와 추적 설정 API가 다른 것은 의도다.** 여기서는 원격 브랜치가 *이미 있으므로*
> `TrackedBranch`에 그 정식 이름을 넣으면 끝난다. Task 5는 원격에 브랜치가 *아직 없어*
> 먼저 push한 뒤 `Remote`와 `UpstreamBranch`를 따로 세운다. 한쪽을 다른 쪽 모양으로
> "통일"하지 말 것.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SwitchBranch"`
Expected: PASS (4건)

- [ ] **Step 6: 전체 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: 전부 PASS. 여기서 깨지는 것이 있으면 `RetrieveStatus` 열거가 다른 테스트의 임시 저장소와 부딪히는 것이니 그 테스트를 먼저 읽는다.

- [ ] **Step 7: 커밋한다**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 작업 트리가 깨끗할 때만 브랜치를 전환한다"
```

---

### Task 5: 첫 Push가 추적을 설정한다

**Files:**
- Modify: `src/DBVC.Core/Models/PushResult.cs`, `src/DBVC.Core/Abstractions.cs`, `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs` (기존 테스트 하나를 고친다)

**Interfaces:**
- Consumes: 없음
- Produces:
  - `PushResult.NoUpstream`
  - `PushResult IGitManager.PushChanges(string serverName, string databaseName, bool setUpstream = false)`

- [ ] **Step 1: 옛 동작을 검증하는 테스트를 새 동작으로 고친다**

`PushChanges_ExplainsInKorean_WhenTheCurrentBranchHasNoUpstream`을 **지우지 않고** 아래로 바꾼다. 무엇이 언제 왜 바뀌었는지 이 테스트가 이력에 남긴다.

```csharp
        [Test]
        public void PushChanges_ReturnsNoUpstream_WhenTheCurrentBranchHasNoUpstream()
        {
            // 0.5.21까지는 GitRemoteNotConfiguredException으로 막고 터미널로 돌려보냈다.
            // 첫 Push는 예외적 사건이 아니라 정상 흐름이므로 결과값으로 낸다 - 화면이 확인을
            // 받아 setUpstream: true로 다시 부른다.
            var originPath = NewRepoWithCommit();
            var localPath = NewRepoWithCommit();
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", originPath);
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            Assert.That(git.PushChanges("localhost", "testdb"), Is.EqualTo(PushResult.NoUpstream));
        }
```

- [ ] **Step 2: upstream 설정 테스트를 더한다**

```csharp
        [Test]
        public void PushChanges_SetsUpstream_WhenSetUpstreamIsTrue()
        {
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            string branchName;
            using (var local = new Repository(localPath))
            {
                var created = local.CreateBranch("PROJ-123");
                Commands.Checkout(local, created);
                branchName = created.FriendlyName;
                Assert.That(local.Head.IsTracking, Is.False, "전제: 아직 추적이 없습니다");
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var result = git.PushChanges("localhost", "testdb", setUpstream: true);

            Assert.That(result, Is.EqualTo(PushResult.Pushed));
            using var after = new Repository(localPath);
            Assert.That(after.Head.IsTracking, Is.True);
            using var origin = new Repository(originPath);
            Assert.That(origin.Branches[branchName], Is.Not.Null, "원격에 브랜치가 생겨야 합니다");
        }
```

- [ ] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PushChanges"`
Expected: 컴파일 실패 — `PushResult.NoUpstream`과 `setUpstream` 매개변수가 없다.

- [ ] **Step 4: 결과값을 더한다**

`src/DBVC.Core/Models/PushResult.cs`의 `Pushed` 아래:

```csharp
        /// <summary>
        /// 현재 브랜치에 추적 중인 원격 브랜치가 없다. 오류가 아니라 새 브랜치의 첫 Push다.
        /// 화면이 확인을 받아 setUpstream: true로 다시 부른다.
        /// </summary>
        NoUpstream
```

- [ ] **Step 5: 시그니처를 넓힌다**

`Abstractions.cs`:

```csharp
        /// <summary>
        /// 로컬 커밋을 원격에 올린다. 추적 중인 원격 브랜치가 없으면
        /// <see cref="PushResult.NoUpstream"/>을 돌려주고 아무것도 하지 않는다.
        /// <paramref name="setUpstream"/>이 true이면 원격에 브랜치를 만들고 추적을 설정한다.
        /// </summary>
        PushResult PushChanges(string serverName, string databaseName, bool setUpstream = false);
```

- [ ] **Step 6: 구현한다**

`GitManager.PushChanges`에서 `ValidateRemoteAndBuildGuidance` 호출 **앞**에 갈래를 넣는다. 순서가 곧 정확성이다 — 뒤에 두면 공용 검사가 먼저 예외를 던진다.

```csharp
            using var repo = new Repository(repoPath);

            // 공용 검사보다 먼저 본다. ValidateRemoteAndBuildGuidance는 추적이 없으면
            // 예외를 던지는데, Push에서는 그것이 오류가 아니라 "첫 Push"라는 정상 상태다.
            // Pull은 그대로 던진다 - 추적이 없으면 받아올 대상 자체가 없어 안내가 종착점이다.
            if (!repo.Head.IsTracking && repo.Network.Remotes.Any())
            {
                if (!setUpstream) return PushResult.NoUpstream;

                var remote = ResolvePushRemote(repo);
                if (remote == null)
                {
                    throw new GitRemoteNotConfiguredException(
                        $"'{repoPath}' 저장소에 원격이 여럿이라 어디에 올릴지 정할 수 없습니다. " +
                        "Git 클라이언트에서 'git push -u <원격> " + repo.Head.FriendlyName + "'을 한 번 실행하세요.");
                }

                var branch = repo.Head;
                var pushOptions = BuildPushOptions(() => { }, _ => { });
                repo.Network.Push(remote, branch.CanonicalName + ":" + branch.CanonicalName, pushOptions);
                repo.Branches.Update(branch,
                    b => b.Remote = remote.Name,
                    b => b.UpstreamBranch = branch.CanonicalName);
                return PushResult.Pushed;
            }
```

그리고 원격 고르기를 private으로 둔다.

```csharp
        /// <summary>
        /// 추적이 없는 브랜치를 올릴 원격을 고른다. origin이 있으면 그것, 없고 원격이 하나뿐이면
        /// 그것, 여럿이면 null이다 - 도구가 임의로 고르면 엉뚱한 곳에 브랜치가 생긴다.
        /// </summary>
        private static Remote? ResolvePushRemote(Repository repo)
        {
            var origin = repo.Network.Remotes["origin"];
            if (origin != null) return origin;

            var all = repo.Network.Remotes.ToList();
            return all.Count == 1 ? all[0] : null;
        }
```

- [ ] **Step 7: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PushChanges"`
Expected: PASS

- [ ] **Step 8: 전체 Core 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: 전부 PASS

- [ ] **Step 9: 커밋한다**

```bash
git add src/DBVC.Core/Models/PushResult.cs src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 첫 Push가 원격 브랜치를 만들고 추적을 설정한다"
```

---

### Task 6: 화면이 브랜치를 만들고 갈아탄다

**Files:**
- Create: `src/DBVC.Vsix/Services/IBranchDialog.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs`, `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitManager.GetBranches/CreateBranch/SwitchBranch` (Task 2·3·4), `MappingPolicy.IsAllowed` (Task 1)
- Produces:
  - `interface IBranchDialog { string? AskNewName(); string? AskExisting(IReadOnlyList<BranchInfo> branches); }`
  - `ViewChangesViewModel.CreateBranchCommand`, `ViewChangesViewModel.SwitchBranchCommand`

- [ ] **Step 1: 대역과 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs`에 더한다.

```csharp
    internal sealed class RecordingBranchDialog : IBranchDialog
    {
        public string? NewNameToReturn { get; set; }
        public string? ExistingToReturn { get; set; }
        public IReadOnlyList<BranchInfo>? OfferedBranches { get; private set; }

        public string? AskNewName() => NewNameToReturn;

        public string? AskExisting(IReadOnlyList<BranchInfo> branches)
        {
            OfferedBranches = branches;
            return ExistingToReturn;
        }
    }
```

`ViewChangesViewModelTests.cs`에 더한다. `NewConnectedViewModel()`이 이미 있고 `_git`은 `Mock<IGitManager>`다.

```csharp
        [Test]
        public void CreateBranchCommand_CreatesAndRefreshes_WhenNameIsGiven()
        {
            _branchDialog.NewNameToReturn = "PROJ-123";
            _git.Setup(g => g.CreateBranch(Server, Database, "PROJ-123")).Returns(BranchResult.Ok());
            var vm = NewConnectedViewModel();

            vm.CreateBranchCommand.Execute(null);

            _git.Verify(g => g.CreateBranch(Server, Database, "PROJ-123"), Times.Once);
        }

        [Test]
        public void CreateBranchCommand_DoesNothing_WhenDialogIsCancelled()
        {
            _branchDialog.NewNameToReturn = null;
            var vm = NewConnectedViewModel();

            vm.CreateBranchCommand.Execute(null);

            _git.Verify(g => g.CreateBranch(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void SwitchBranchCommand_ShowsBlockingPaths_WhenTreeIsDirty()
        {
            _branchDialog.ExistingToReturn = "develop";
            _git.Setup(g => g.GetBranches(Server, Database))
                .Returns(new[] { new BranchInfo { Name = "develop" } });
            _git.Setup(g => g.SwitchBranch(Server, Database, "develop"))
                .Returns(BranchResult.Fail("커밋되지 않은 변경이 있어 브랜치를 바꿀 수 없습니다.",
                    new[] { "dbo/Tables/Orders.sql" }));
            var vm = NewConnectedViewModel();

            vm.SwitchBranchCommand.Execute(null);

            Assert.That(_notifier.Errors.Single(), Does.Contain("dbo/Tables/Orders.sql"),
                "무엇이 막았는지 사용자가 바로 보여야 합니다");
        }

        [TestCase(MappingMode.Deploy)]
        [TestCase(MappingMode.Audit)]
        public void BranchCommands_CannotExecute_WhenCloneIsPinned(MappingMode mode)
        {
            var vm = NewConnectedViewModelWithMode(mode);

            Assert.That(vm.CreateBranchCommand.CanExecute(null), Is.False);
            Assert.That(vm.SwitchBranchCommand.CanExecute(null), Is.False);
        }
```

> **확인된 사실:** `RecordingNotifier`에는 `Errors`(message만), `ErrorCalls`(title+message),
> `ConfirmResult`, `ConfirmCallCount`, `ConfirmCalls`가 이미 있다. 새로 더할 것은 없다.
>
> **스케줄러를 돌려야 한다.** 브랜치 조작은 `_scheduler`를 타므로, 테스트가 쓰는
> `DeferredBackgroundScheduler`를 기존 비동기 테스트와 같은 방식으로 펌프해야 결과 콜백이
> 실행된다. 그 방법은 `ViewChangesViewModelTests`의 Push·Pull 테스트를 그대로 본뜬다.
>
> `NewConnectedViewModelWithMode`는 없다. `NewConnectedViewModel()`을 본떠 `_config`가 그
> mode의 `MappingConfig`를 내도록 만든다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~Branch"`
Expected: 컴파일 실패 — `IBranchDialog`와 두 명령이 없다.

- [ ] **Step 3: 이음매를 만든다**

`src/DBVC.Vsix/Services/IBranchDialog.cs`

```csharp
using System.Collections.Generic;
using DBVC.Core.Models;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 브랜치 이름을 받고 고르게 하는 창. 인터페이스로 두는 이유는 나머지 대화상자와 같다 -
    /// WPF 창을 띄우지 않고 ViewModel을 테스트하기 위해서다.
    /// </summary>
    public interface IBranchDialog
    {
        /// <summary>새 브랜치 이름을 받는다. 취소하면 null이다.</summary>
        string? AskNewName();

        /// <summary>갈아탈 브랜치를 고르게 한다. 취소하면 null이다.</summary>
        string? AskExisting(IReadOnlyList<BranchInfo> branches);
    }
}
```

- [ ] **Step 4: ViewModel에 명령을 더한다**

생성자에 `IBranchDialog? branchDialog = null`을 **맨 끝 선택 매개변수**로 더한다(`identityDialog`와 같은 자리). 그리고:

```csharp
        public ICommand CreateBranchCommand { get; }
        public ICommand SwitchBranchCommand { get; }
```

생성자 안:

```csharp
            CreateBranchCommand = new RelayCommand(CreateBranch, CanChangeBranch);
            SwitchBranchCommand = new RelayCommand(SwitchBranch, CanChangeBranch);
```

본문:

```csharp
        /// <summary>
        /// 고정 브랜치가 있는 클론에서는 아예 누를 수 없다. 판정은 Core와 같은 함수를 쓴다 -
        /// 화면이 따로 판정하면 언젠가 갈라지고, 갈라진 쪽이 이기는 날 사고가 난다.
        /// </summary>
        private bool CanChangeBranch()
        {
            if (IsBusy || !IsMapped || _branchDialog == null) return false;
            var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
            return mapping != null && MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.CreateBranch);
        }

        /// <summary>브랜치 조작의 결과와, 그 뒤 화면이 새로 그려야 할 저장소 상태를 함께 나른다.</summary>
        private sealed class BranchOutcome
        {
            public BranchResult Result { get; set; } = BranchResult.Ok();
            public RepositoryState? State { get; set; }
        }

        private void CreateBranch()
        {
            if (!CanChangeBranch()) return;

            var name = _branchDialog!.AskNewName();
            if (string.IsNullOrWhiteSpace(name)) return;

            RunBranchOperation("브랜치를 만드는 중...",
                (server, database) => _gitManager.CreateBranch(server, database, name!));
        }

        private void SwitchBranch()
        {
            if (!CanChangeBranch()) return;

            // 목록 읽기도 libgit2가 도는 일이지만, 대화상자를 띄우려면 UI 스레드에 값이 있어야
            // 한다. 로컬 참조만 훑는 짧은 작업이라 여기서만 예외로 둔다 - 네트워크는 타지 않는다.
            var branches = _gitManager.GetBranches(ServerName!, DatabaseName!);
            if (branches.Count == 0)
            {
                _notifier.ShowError("DBVC 브랜치 전환", "이 저장소에서 브랜치를 읽지 못했습니다.");
                return;
            }

            var name = _branchDialog!.AskExisting(branches);
            if (string.IsNullOrWhiteSpace(name)) return;

            RunBranchOperation("브랜치를 바꾸는 중...",
                (server, database) => _gitManager.SwitchBranch(server, database, name!));
        }

        /// <summary>
        /// 브랜치 조작을 UI 스레드 밖에서 돌린다. libgit2가 도는 일을 UI 스레드에서 부르면
        /// 개체 탐색기를 붙잡는다 - 접속 판정과 저장소 상태 읽기를 백그라운드로 뺀 이유와 같다.
        /// 상태 읽기를 같은 작업에 묶는 이유는, 성공하면 CurrentBranch가 반드시 함께 바뀌어야
        /// 하는데 그것을 읽는 것도 저장소를 여는 일이기 때문이다.
        /// </summary>
        private void RunBranchOperation(string progress, Func<string, string, BranchResult> operation)
        {
            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            ProgressText = progress;

            _scheduler.Run(
                () =>
                {
                    var result = operation(server, database);
                    return new BranchOutcome
                    {
                        Result = result,
                        State = result.Succeeded ? _gitManager.GetRepositoryState(server, database) : null
                    };
                },
                ApplyBranchOutcome,
                ex =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    _notifier.ShowError("DBVC 브랜치", ex.Message);
                });
        }

        /// <summary>
        /// 브랜치가 바뀌면 비교 기준이 통째로 바뀐다. 변경 목록을 그대로 두면 옛 브랜치 기준의
        /// 상태가 새 브랜치의 것인 척 남는다 - 그래서 성공하면 반드시 다시 읽는다.
        /// UI 스레드에서만 불린다.
        /// </summary>
        private void ApplyBranchOutcome(BranchOutcome outcome)
        {
            IsBusy = false;
            ProgressText = null;

            if (!outcome.Result.Succeeded)
            {
                var detail = outcome.Result.BlockingPaths.Count == 0
                    ? outcome.Result.Message
                    : outcome.Result.Message + Environment.NewLine + Environment.NewLine +
                      string.Join(Environment.NewLine, outcome.Result.BlockingPaths);
                _notifier.ShowError("DBVC 브랜치", detail ?? "브랜치를 바꾸지 못했습니다.");
                return;
            }

            // CurrentBranch는 연결 직후(488행 근처) 한 곳에서만 대입된다. 브랜치를 바꿔 놓고
            // 여기서 갱신하지 않으면 화면이 옛 브랜치 이름을 계속 보여 준다.
            CurrentBranch = outcome.State?.CurrentBranch;

            // 인자 없는 Refresh()는 없다. 전체 추출이 아니라 로그가 아는 것만 다시 뽑는다.
            Refresh(fullExtraction: false);
        }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~Branch"`
Expected: PASS

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/Services/IBranchDialog.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/
git commit -m "feat(vsix): 브랜치 만들기·전환 명령을 더한다"
```

---

### Task 7: 첫 Push가 확인을 받는다

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `PushResult.NoUpstream`, `PushChanges(..., setUpstream)` (Task 5), `IUserNotifier.Confirm`
- Produces: 없음 — 기존 Push 명령의 갈래다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Test]
        public void PushCommand_AsksBeforeSettingUpstream_WhenBranchHasNoUpstream()
        {
            _git.Setup(g => g.PushChanges(Server, Database, false)).Returns(PushResult.NoUpstream);
            _git.Setup(g => g.PushChanges(Server, Database, true)).Returns(PushResult.Pushed);
            _notifier.ConfirmResult = true;
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Single().Message, Does.Contain("추적"),
                "무엇을 바꾸는지 먼저 말해야 합니다");
            _git.Verify(g => g.PushChanges(Server, Database, true), Times.Once);
        }

        [Test]
        public void PushCommand_DoesNotSetUpstream_WhenUserDeclines()
        {
            _git.Setup(g => g.PushChanges(Server, Database, false)).Returns(PushResult.NoUpstream);
            _notifier.ConfirmResult = false;
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            _git.Verify(g => g.PushChanges(Server, Database, true), Times.Never);
        }
```

> `ConfirmResult`(기본값 true)와 `ConfirmCalls`는 `RecordingNotifier`에 이미 있다. 더할 것 없다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~PushCommand"`
Expected: FAIL — `NoUpstream`이 처리되지 않아 확인이 뜨지 않는다.

- [ ] **Step 3: `ApplyPushResult`에 갈래를 더한다**

배경 스케줄러의 결과 콜백은 UI 스레드에서 불리므로 여기서 확인을 띄울 수 있다. 되돌리기·무시가 쓰는 왕복 패턴과 같다.

```csharp
            if (result == PushResult.NoUpstream)
            {
                var branch = CurrentBranch ?? "현재 브랜치";
                var ok = _notifier.Confirm(
                    "DBVC Push",
                    $"'{branch}' 브랜치는 아직 원격에 없습니다." + Environment.NewLine +
                    $"원격에 '{branch}'를 만들고 이 브랜치가 그것을 추적하도록 설정합니다." + Environment.NewLine +
                    Environment.NewLine +
                    "이 저장소의 .git/config만 바뀌며 다른 저장소에는 영향이 없습니다.");

                if (!ok) return;

                PushWithUpstream();
                return;
            }
```

그리고 확인을 받은 뒤 같은 경로를 다시 타는 메서드를 둔다.

```csharp
        /// <summary>
        /// 확인을 받은 뒤의 두 번째 Push. Push()와 같은 배선을 쓰되 setUpstream만 참이다 -
        /// 여기서 다시 NoUpstream이 오는 일은 없으므로 그 갈래를 타지 않는다.
        /// </summary>
        private void PushWithUpstream()
        {
            var server = ServerName!;
            var database = DatabaseName!;

            IsBusy = true;
            ProgressText = "원격에 브랜치를 만드는 중...";

            _scheduler.Run(
                () => _gitManager.PushChanges(server, database, setUpstream: true),
                ApplyPushResult,
                ex =>
                {
                    IsBusy = false;
                    ProgressText = null;
                    _notifier.ShowError("DBVC Push 실패", ex.Message);
                });
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~PushCommand"`
Expected: PASS

- [ ] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: 전부 PASS

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/
git commit -m "feat(vsix): 첫 Push가 추적 설정을 묻는다"
```

---

### Task 8: 버튼과 대화상자를 붙인다

**WPF는 CI가 검증하지 못한다.** 이 작업의 검증은 SSMS 21에서 직접 누르는 것이다.

**Files:**
- Create: `src/DBVC.Vsix/UI/BranchDialog.xaml`, `src/DBVC.Vsix/UI/BranchDialog.xaml.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`, `src/DBVC.Vsix/DbvcServices.cs`

**Interfaces:**
- Consumes: `IBranchDialog` (Task 6), `CreateBranchCommand`·`SwitchBranchCommand` (Task 6)
- Produces: `BranchDialog : IBranchDialog`

- [ ] **Step 1: 대화상자를 만든다**

`CommitIdentityDialog.xaml`을 본으로 삼는다 — 크기·여백·버튼 배치가 그쪽과 같아야 한 벌로 보인다. 한 창에 두 모드를 두고 `Visibility`로 가른다.

`src/DBVC.Vsix/UI/BranchDialog.xaml`

```xml
<Window x:Class="DBVC.Vsix.UI.BranchDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="DBVC 브랜치" Width="420" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False">
    <StackPanel Margin="16">
        <!-- 새 이름 모드 -->
        <StackPanel x:Name="NewNamePanel">
            <TextBlock Text="새 브랜치 이름" FontWeight="SemiBold" Margin="0,0,0,4"/>
            <TextBox x:Name="NameBox" Margin="0,0,0,8"/>
            <TextBlock TextWrapping="Wrap" Foreground="Gray" Margin="0,0,0,12"
                       Text="지금 브랜치에서 갈라져 나옵니다. 작업 중인 파일은 그대로 남습니다."/>
        </StackPanel>

        <!-- 고르기 모드 -->
        <StackPanel x:Name="PickPanel" Visibility="Collapsed">
            <TextBlock Text="갈아탈 브랜치" FontWeight="SemiBold" Margin="0,0,0,4"/>
            <ListBox x:Name="BranchList" Height="180" Margin="0,0,0,8"
                     DisplayMemberPath="Display"/>
            <TextBlock TextWrapping="Wrap" Foreground="Gray" Margin="0,0,0,12"
                       Text="커밋되지 않은 변경이 있으면 갈아타지 않고 무엇이 남았는지 알려 줍니다."/>
        </StackPanel>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="확인" Width="80" Margin="0,0,8,0" IsDefault="True" Click="OnOk"/>
            <Button Content="취소" Width="80" IsCancel="True"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`src/DBVC.Vsix/UI/BranchDialog.xaml.cs`

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DBVC.Core.Models;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.UI
{
    public partial class BranchDialog : Window, IBranchDialog
    {
        /// <summary>목록에 그대로 뿌릴 한 줄. BranchInfo에 표시 문자열을 넣지 않는 이유는
        /// Core가 화면 문구를 갖지 않는다는 규약 때문이다.</summary>
        private sealed class Row
        {
            public string Name { get; set; } = string.Empty;
            public string Display { get; set; } = string.Empty;
        }

        public BranchDialog() { InitializeComponent(); }

        public string? AskNewName()
        {
            NewNamePanel.Visibility = Visibility.Visible;
            PickPanel.Visibility = Visibility.Collapsed;
            NameBox.Focus();
            return ShowDialog() == true ? NameBox.Text?.Trim() : null;
        }

        public string? AskExisting(IReadOnlyList<BranchInfo> branches)
        {
            NewNamePanel.Visibility = Visibility.Collapsed;
            PickPanel.Visibility = Visibility.Visible;

            BranchList.ItemsSource = branches.Select(b => new Row
            {
                Name = b.Name,
                Display = b.Name
                    + (b.IsCurrent ? "  (현재)" : string.Empty)
                    + (b.IsRemoteOnly ? "  (원격)" : string.Empty)
            }).ToList();

            // 현재 브랜치로 갈아탈 이유는 없으므로 처음부터 고르지 않는다.
            return ShowDialog() == true ? (BranchList.SelectedItem as Row)?.Name : null;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            // 빈 이름·미선택으로 닫히면 호출자가 null을 받아 조용히 아무것도 하지 않는다.
            DialogResult = true;
        }
    }
}
```

- [ ] **Step 2: 도구 창에 버튼을 더한다**

`ViewChangesControl.xaml`에서 `VersionLabel`·`브랜치:` 표시가 있는 `DockPanel`(46~60행 근처)에 버튼 둘을 붙인다.

```xml
                    <Button Content="새 브랜치" Command="{Binding CreateBranchCommand}" Width="80" Margin="0,0,6,4"
                            ToolTip="지금 브랜치에서 새 브랜치를 만들고 갈아탑니다. 작업 중인 파일은 그대로 남습니다."/>
                    <Button Content="브랜치 전환" Command="{Binding SwitchBranchCommand}" Width="90" Margin="0,0,10,4"
                            ToolTip="다른 브랜치로 갈아탑니다.&#10;커밋되지 않은 변경이 있으면 갈아타지 않고 무엇이 남았는지 알려 줍니다."/>
```

- [ ] **Step 3: 조립 루트에 배선한다**

`DbvcServices.cs`에서 `ViewChangesViewModel`을 만드는 자리에 `branchDialog: new BranchDialog()`를 더한다. 다른 대화상자와 같은 방식이다.

- [ ] **Step 4: 빌드한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공. `.vsix`까지 확인하려면
`dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release` 뒤 `dir src\DBVC.Vsix\bin\Release\net48\*.vsix`.
**빌드 성공 ≠ `.vsix` 생성이므로 산출물 존재를 반드시 눈으로 본다.**

- [ ] **Step 5: SSMS 21에서 손으로 확인한다**

이 넷을 밟기 전에는 "동작한다"고 말하지 않는다.

- [ ] `develop`에서 **새 브랜치** → 미커밋 추출물이 그대로 남아 있고 브랜치 표시가 바뀐다
- [ ] 커밋 → **Push** → 확인 대화상자가 뜨고, 동의하면 원격에 브랜치가 생긴다. **두 번째 Push는 묻지 않는다**
- [ ] 추출물이 남은 채 **브랜치 전환** → 거부되고 파일 이름이 보인다
- [ ] 배포 용도로 연결한 클론에서 두 버튼이 **비활성이거나 뜨지 않는다**

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/UI/BranchDialog.xaml src/DBVC.Vsix/UI/BranchDialog.xaml.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/DbvcServices.cs
git commit -m "feat(vsix): 브랜치 버튼과 대화상자를 붙인다"
```

---

### Task 9: 문서와 버전

**Files:**
- Modify: `docs/user-guide.html`, `docs/rollout-announcement.md`, `README.md`, `src/DBVC.Vsix/source.extension.vsixmanifest`

**Interfaces:**
- Consumes: 앞의 모든 동작
- Produces: 없음

- [ ] **Step 1: 사용 설명서를 고친다**

`docs/user-guide.html` 2장에 티켓 한 바퀴를 `.steps.stack`으로 넣는다. 2.5절의 Push 줄에서 "추적 브랜치가 없으면 거부된다"를 새 동작으로 바꾼다. 4장 증상표의 관련 줄도 함께.

**인라인 태그 뒤에 줄바꿈을 두지 않는다** — 조사가 떨어져 "새로고침 에서"가 된다.

- [ ] **Step 2: 배포 공지를 고친다**

`docs/rollout-announcement.md` 2절 "규칙"에 티켓 키 규칙을 더한다.

```markdown
**브랜치 이름과 커밋 메시지 둘 다 지라 티켓 키로 시작한다.** 브랜치는 `PROJ-123`,
커밋 메시지는 `PROJ-123: 주문 상세에 할인율 컬럼을 더한다`. 연동이 어느 쪽을 긁어 가든
걸린다. **스키마 저장소에도 GitLab↔지라 연동을 따로 걸어야 한다** — 소스 코드 저장소와
다른 저장소라 자동으로 따라오지 않는다.
```

- [ ] **Step 3: 커진 위험을 릴리스 노트에 적는다**

스펙 6절이 "배포할 때 다시 짚는다"고 한 것이다. `docs/rollout-announcement.md` 맨 아래
"릴리스 노트 템플릿"의 `### 브랜치` 절에 한 줄을 더한다.

```markdown
브랜치를 만들기 쉬워진 만큼 오래 들고 있기도 쉬워집니다. 오래 들수록 같은 객체를
남이 만질 확률이 커지고, 그만큼 남의 변경이 내 커밋에 딸려 옵니다. **티켓을 빨리
닫는 것 말고 이 위험을 줄이는 방법은 없습니다.**
```

- [ ] **Step 4: README를 고친다**

"동작 방식" 목록에 브랜치 만들기·전환과 첫 Push 확인을 한 줄씩 더한다.

- [ ] **Step 5: 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest`의 `Version`을 `0.5.21` → `0.6.0`으로. 사용자 눈에 보이는 동작이 늘었으므로 minor를 올린다. **버전을 올리지 않은 `.vsix`는 받는 쪽의 설치가 거부된다.**

- [ ] **Step 6: 아티팩트를 다시 발행한다**

`docs/user-guide.html`에서 발행용 사본을 만들어 같은 URL(`f54a7524-954e-4716-9772-76d79cddfa3f`)로 다시 올린다. 껍데기(`<!doctype>`·`<head>`·`<body>`)를 벗기고 폰트 `<link>`를 평범한 것으로 바꾸는 변환은 저장소 파일에서 매번 다시 만든다.

- [ ] **Step 7: 커밋한다**

```bash
git add docs/ README.md src/DBVC.Vsix/source.extension.vsixmanifest
git commit -m "docs: 브랜치 조작을 문서에 반영하고 0.6.0으로 올린다"
```

---

## 실행 순서와 의존

```
Task 1 (정책)
  ├─ Task 2 (목록) ─┐
  ├─ Task 3 (생성) ─┤
  └─ Task 4 (전환) ─┴─ Task 6 (명령) ─┐
     Task 5 (Push)  ─────────────────┴─ Task 7 (Push 확인) ─ Task 8 (UI) ─ Task 9 (문서)
```

Task 2·3·4·5는 서로 독립이라 순서를 바꿔도 된다. Task 4는 Task 2의 `StripRemotePrefix`를 쓰므로 2보다 뒤여야 한다.
