# 작업 트리 되돌리기 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 변경 목록에서 고른 `.sql`을 SSMS 안에서 저장소의 마지막 커밋 내용으로 되돌린다.

**Architecture:** 판정은 Core의 순수 함수 `DiscardPlan` 한 곳에 둔다. 화면은 그것으로 확인 문구를 만들고 `GitManager.DiscardChanges`는 그것으로 실행 대상을 정한다. 되돌리기는 파일까지만 하고 `DBVC_ChangeLog`는 건드리지 않는다. 되돌린 뒤에는 재추출 없이 목록만 갱신한다 — 지금의 `Refresh()`를 부르면 같은 클릭 안에서 되돌리기가 취소된다.

**Tech Stack:** .NET (netstandard2.0 + net48), LibGit2Sharp 0.32.0, WPF/MVVM, NUnit + Moq

**Spec:** `docs/superpowers/specs/2026-09-07-dbvc-discard-changes-design.md`

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지·알림·버튼·ToolTip 포함. Core는 상태를 영어 식별자로 다루고 화면 계층에서만 한국어로 옮긴다.
- **주석은 "왜"만 적는다.** 한국어 평서문. 함정과 근거를 남기는 기존 문체를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): 작업 트리 되돌리기를 더한다`.
- **TDD**: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋. 테스트 이름은 영어 `Method_Result_WhenCondition`.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0`, `LibGit2Sharp 0.32.0` 고정.
- **테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다.** 전이 참조로만 받는다.
- 검증 명령: `dotnet build DBVC.slnx`, `dotnet test tests/DBVC.Core.Tests`, `dotnet test tests/DBVC.Vsix.Tests`.
- 테스트 프로젝트는 `net48;net10.0` 멀티타깃이다. 프레임워크를 좁히려면 `-f net10.0`을 쓴다.

---

### Task 1: `DiscardPlan` — 판정의 유일한 자리

**Files:**
- Create: `src/DBVC.Core/DiscardPlan.cs`
- Test: `tests/DBVC.Core.Tests/DiscardPlanTests.cs`

**Interfaces:**
- Consumes: `ObjectPathConvention.TryParseRelativePath(string?, out string, out string, out string)`
- Produces:
  - `DBVC.Core.DiscardPlan` — `List<string> RestorePaths`, `List<string> DeletePaths`, `List<string> SkippedPaths`, `bool IsEmpty`
  - `static DiscardPlan DiscardPlan.Build(IEnumerable<string>? requestedPaths, IReadOnlyDictionary<string, string>? gitStates)`
  - `DBVC.Core.DiscardResult` — `List<string> RestoredPaths`, `List<string> DeletedPaths`, `List<string> FailedPaths`, `List<string> SkippedPaths`, `bool HasFailures`

`gitStates`는 `IGitManager.GetChangedFileStates`가 내는 사전이다. 값은 `"Added"` / `"Modified"` / `"Deleted"` 셋 중 하나이고, 키는 `dbo/Tables/Users.sql` 형태의 `/` 구분 상대 경로다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/DiscardPlanTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 되돌리기의 판정은 여기 하나뿐이다. 화면과 GitManager가 각자 판정하면 갈라지고,
    /// 갈라지는 날 "지울 파일 0개"라고 말해 놓고 지운다.
    /// </summary>
    [TestFixture]
    public class DiscardPlanTests
    {
        private static Dictionary<string, string> States(params (string Path, string State)[] entries)
        {
            var states = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries) states[entry.Path] = entry.State;
            return states;
        }

        [TestCase("Modified")]
        [TestCase("Deleted")]
        public void Build_PutsPathInRestore_WhenGitReportsTrackedChange(string state)
        {
            var plan = DiscardPlan.Build(
                new[] { "dbo/Tables/Users.sql" },
                States(("dbo/Tables/Users.sql", state)));

            Assert.That(plan.RestorePaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
            Assert.That(plan.DeletePaths, Is.Empty);
        }

        [Test]
        public void Build_PutsPathInDelete_WhenGitReportsAdded()
        {
            var plan = DiscardPlan.Build(
                new[] { "dbo/Views/vSales.sql" },
                States(("dbo/Views/vSales.sql", "Added")));

            Assert.That(plan.DeletePaths, Is.EqualTo(new[] { "dbo/Views/vSales.sql" }));
            Assert.That(plan.RestorePaths, Is.Empty);
        }

        /// <summary>
        /// 화면 목록은 사용자가 들여다본 만큼 낡는다. 그 사이에 깨끗해진 파일을 HEAD로
        /// 덮어쓰는 것은 되돌리기가 아니라 손실이다.
        /// </summary>
        [Test]
        public void Build_SkipsPath_WhenGitNoLongerReportsIt()
        {
            var plan = DiscardPlan.Build(
                new[] { "dbo/Tables/Users.sql" },
                States(("dbo/Tables/Orders.sql", "Modified")));

            Assert.That(plan.RestorePaths, Is.Empty);
            Assert.That(plan.DeletePaths, Is.Empty);
            Assert.That(plan.SkippedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
        }

        /// <summary>DBVC가 만든 파일이 아니면 되돌리기의 대상이 아니다.</summary>
        [TestCase("README.md")]
        [TestCase(".gitattributes")]
        [TestCase("dbo/Tables/Users.txt")]
        public void Build_SkipsPath_WhenItIsNotADbvcObjectFile(string path)
        {
            var plan = DiscardPlan.Build(new[] { path }, States((path, "Modified")));

            Assert.That(plan.SkippedPaths, Is.EqualTo(new[] { path }));
        }

        /// <summary>
        /// ".." 세 조각은 경로 규약 검사를 그대로 통과한다(WorkingTreeCleaner에 같은 함정이
        /// 기록되어 있다). 여기서 걸러 두어야 저장소 밖 파일이 대상이 되지 않는다.
        /// </summary>
        [Test]
        public void Build_SkipsPath_WhenItEscapesTheRepositoryRoot()
        {
            var plan = DiscardPlan.Build(
                new[] { "../../evil.sql" },
                States(("../../evil.sql", "Modified")));

            Assert.That(plan.SkippedPaths, Is.EqualTo(new[] { "../../evil.sql" }));
            Assert.That(plan.RestorePaths, Is.Empty);
        }

        [Test]
        public void Build_ReturnsEmptyPlan_WhenNothingIsEligible()
        {
            var plan = DiscardPlan.Build(new[] { "README.md" }, States(("README.md", "Modified")));

            Assert.That(plan.IsEmpty, Is.True);
        }

        [Test]
        public void Build_ReturnsEmptyPlan_WhenInputsAreNull()
        {
            var plan = DiscardPlan.Build(null, null);

            Assert.That(plan.IsEmpty, Is.True);
            Assert.That(plan.SkippedPaths, Is.Empty);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DiscardPlanTests"`
Expected: 컴파일 실패 — `DiscardPlan`이 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/DiscardPlan.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace DBVC.Core
{
    /// <summary>
    /// 되돌리기의 대상을 가른다. Git도 DB도 닿지 않는 순수 함수라 판정의 시험대가 여기 하나다.
    ///
    /// 화면은 이것으로 확인 문구를 만들고 GitManager는 이것으로 실행 대상을 정한다.
    /// 판정이 두 곳에 생기면 갈라지고, 갈라지는 날 "지울 파일 0개"라 말해 놓고 지운다.
    /// (MappingPolicy를 CanExecute와 Core 진입부가 함께 부르는 것과 같은 구조다.)
    /// </summary>
    public class DiscardPlan
    {
        private const string AddedState = "Added";

        /// <summary>추적 중인 파일. HEAD 내용으로 되돌린다.</summary>
        public List<string> RestorePaths { get; } = new List<string>();

        /// <summary>미추적 파일. 지우는 것 외에 되돌릴 방법이 없다.</summary>
        public List<string> DeletePaths { get; } = new List<string>();

        /// <summary>대상이 아닌 경로. 사용자가 체크한 것이 조용히 빠지면 안 되므로 남긴다.</summary>
        public List<string> SkippedPaths { get; } = new List<string>();

        public bool IsEmpty => RestorePaths.Count == 0 && DeletePaths.Count == 0;

        public static DiscardPlan Build(
            IEnumerable<string>? requestedPaths,
            IReadOnlyDictionary<string, string>? gitStates)
        {
            var plan = new DiscardPlan();

            foreach (var path in requestedPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                string? state = null;
                gitStates?.TryGetValue(path, out state);

                // Git이 지금 변경으로 보고하지 않는 경로는 되돌릴 것이 없다. 화면 목록은
                // 사용자가 들여다본 만큼 낡으므로, 그 사이에 깨끗해진 파일을 HEAD로 덮어쓰면
                // 되돌리기가 아니라 손실이 된다.
                if (state == null)
                {
                    plan.SkippedPaths.Add(path);
                    continue;
                }

                // DBVC의 경로 규약을 따르지 않는 파일은 DBVC가 만든 것이 아니다.
                // README·.gitattributes·추출 기준선 표식이 이 검사로 보호된다.
                if (!ObjectPathConvention.TryParseRelativePath(path, out _, out _, out _)
                    || EscapesRoot(path))
                {
                    plan.SkippedPaths.Add(path);
                    continue;
                }

                if (string.Equals(state, AddedState, StringComparison.OrdinalIgnoreCase))
                {
                    plan.DeletePaths.Add(path);
                }
                else
                {
                    plan.RestorePaths.Add(path);
                }
            }

            return plan;
        }

        /// <summary>
        /// ".." 세 조각은 경로 규약 검사를 그대로 통과한다(WorkingTreeCleaner에 같은 함정이
        /// 기록되어 있다). 실제 Git 상태에는 이런 경로가 오지 않지만, 판정이 순수 함수인 이상
        /// 입력을 지어낼 수 있는 호출자를 상대로도 성립해야 한다.
        /// </summary>
        private static bool EscapesRoot(string path)
        {
            return path.Replace('\\', '/')
                .Split('/')
                .Any(segment => segment == ".." || segment == ".");
        }
    }

    /// <summary>
    /// 되돌리기의 결과. CleanupResult와 같은 모양이다 — 실패한 경로는 사용자에게 알려야 한다.
    /// </summary>
    public class DiscardResult
    {
        public List<string> RestoredPaths { get; } = new List<string>();
        public List<string> DeletedPaths { get; } = new List<string>();
        public List<string> FailedPaths { get; } = new List<string>();
        public List<string> SkippedPaths { get; } = new List<string>();

        public bool HasFailures => FailedPaths.Count > 0;
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~DiscardPlanTests"`
Expected: PASS (9건)

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/DiscardPlan.cs tests/DBVC.Core.Tests/DiscardPlanTests.cs
git commit -m "feat(core): 되돌리기 대상을 가르는 순수 판정을 더한다"
```

---

### Task 2: `MappingPolicy`에 `Discard`를 더한다

**Files:**
- Modify: `src/DBVC.Core/MappingPolicy.cs`
- Test: `tests/DBVC.Core.Tests/MappingPolicyTests.cs`

**Interfaces:**
- Produces: `DbvcOperation.Discard` — `MappingMode.Write`에서만 허용

배포·감사 클론에는 변경 목록 패널 자체가 없지만(`PanelSelector.cs:22`), Core가 스스로 막아야 한다. 화면과 Core가 각자 판정하면 갈라지고, 갈라진 쪽이 이기는 날 배포 클론에서 파일이 지워진다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/MappingPolicyTests.cs`의 기존 `[TestCase]` 목록에 줄을 더한다.

`IsAllowed_ReturnsTrue_WhenModeIsWrite` 위에:

```csharp
        [TestCase(DbvcOperation.Discard)]
```

`IsAllowed_ReturnsFalse_WhenModeIsNotWrite` 위에:

```csharp
        [TestCase(MappingMode.Deploy, DbvcOperation.Discard)]
        [TestCase(MappingMode.Audit, DbvcOperation.Discard)]
```

그리고 파일 끝(마지막 `}` 두 개 앞)에 문구 테스트를 더한다:

```csharp
        /// <summary>
        /// 거부 문구는 사용자에게 그대로 나간다. 동작 이름이 비어 있으면
        /// "이 대상은 '배포' 용도로 등록되어 있어 을(를) 할 수 없습니다"가 뜬다.
        /// </summary>
        [Test]
        public void BuildDeniedMessage_NamesDiscardInKorean()
        {
            var message = MappingPolicy.BuildDeniedMessage(MappingMode.Deploy, DbvcOperation.Discard);

            Assert.That(message, Does.Contain("작업 트리 되돌리기"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicyTests"`
Expected: 컴파일 실패 — `DbvcOperation.Discard`가 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/DBVC.Core/MappingPolicy.cs`의 `DbvcOperation` enum에 더한다(`GenerateScript` 앞):

```csharp
        /// <summary>작업 트리의 변경을 저장소의 마지막 커밋 내용으로 되돌린다.</summary>
        Discard,
```

`IsAllowed`의 `case DbvcOperation.Push:` 아래에 `case DbvcOperation.Discard:`를 더한다 — 같은 `return mode == MappingMode.Write;`를 탄다. 그 자리의 기존 주석은 커밋에 대한 것이므로, 되돌리기 몫의 주석을 따로 붙인다:

```csharp
                case DbvcOperation.Push:
                // 배포·감사 클론이 더럽다는 것은 DBVC 밖의 무언가가 만졌다는 뜻이다
                // (그쪽은 Extract가 금지되어 있고 CompareWithRepository는 아무것도 쓰지 않는다).
                // 남이 만든 상태를 DBVC가 말없이 치우지 않는다.
                case DbvcOperation.Discard:
                    return mode == MappingMode.Write;
```

`GetOperationName`에 더한다:

```csharp
                case DbvcOperation.Discard: return "작업 트리 되돌리기";
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~MappingPolicyTests"`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/MappingPolicy.cs tests/DBVC.Core.Tests/MappingPolicyTests.cs
git commit -m "feat(core): 되돌리기를 mode 허용 표에 넣는다"
```

---

### Task 3: `IGitManager.DiscardChanges`

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs` (`IGitManager`, `CommitChanges` 선언 아래)
- Modify: `src/DBVC.Core/GitManager.cs` (`CommitChanges` 구현 아래)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Consumes: `DiscardPlan.Build`, `DiscardResult` (Task 1), `DbvcOperation.Discard` (Task 2)
- Produces: `DiscardResult IGitManager.DiscardChanges(string serverName, string databaseName, IEnumerable<string> relativePaths)`

**API 근거 (LibGit2Sharp 0.32.0에서 확인함):**
- `RepositoryExtensions.CheckoutPaths(IRepository, string committishOrBranchSpec, IEnumerable<string> paths)` — 문서에 "Updates specified paths in the **index and working directory**"라고 적혀 있다. 그래서 `Index.Replace`를 따로 부르지 않는다.
- `Repository.RemoveUntrackedFiles()`는 경로 인자가 없다(저장소 전체). 미추적 파일 삭제는 `File.Delete`로 한다.
- `Commands.Unstage(IRepository, IEnumerable<string>)`, `Index.Item(string)` 인덱서, `Repository.Head`, `Branch.Tip` 모두 존재한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs` 끝부분(마지막 `}` 두 개 앞)에 더한다. `NewRepoWithCommit`·`NewRepositoryWithCommit`·`NewGitManager`·`WriteRepoFile`·`TestSignature`·`SeedIdentity`·`NewTempDir`는 이 파일에 이미 있는 헬퍼다.

```csharp
        // ---------- DiscardChanges ----------

        [Test]
        public void DiscardChanges_RestoresFileContent_WhenFileWasModified()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager(Server, Database, repoPath);
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "-- 잘못 추출된 내용");

            var result = git.DiscardChanges(Server, Database, new[] { "dbo/Tables/Users.sql" });

            Assert.That(result.RestoredPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
            Assert.That(
                File.ReadAllText(Path.Combine(repoPath, "dbo", "Tables", "Users.sql")),
                Is.EqualTo("CREATE TABLE Users (Id INT);"));
        }

        [Test]
        public void DiscardChanges_DeletesFile_WhenFileIsUntracked()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager(Server, Database, repoPath);
            WriteRepoFile(repoPath, "dbo/Views/vSales.sql", "CREATE VIEW vSales AS SELECT 1 AS X;");

            var result = git.DiscardChanges(Server, Database, new[] { "dbo/Views/vSales.sql" });

            Assert.That(result.DeletedPaths, Is.EqualTo(new[] { "dbo/Views/vSales.sql" }));
            Assert.That(File.Exists(Path.Combine(repoPath, "dbo", "Views", "vSales.sql")), Is.False);
        }

        [Test]
        public void DiscardChanges_RestoresFile_WhenFileWasDeleted()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager(Server, Database, repoPath);
            File.Delete(Path.Combine(repoPath, "dbo", "Tables", "Users.sql"));

            var result = git.DiscardChanges(Server, Database, new[] { "dbo/Tables/Users.sql" });

            Assert.That(result.RestoredPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
            Assert.That(File.Exists(Path.Combine(repoPath, "dbo", "Tables", "Users.sql")), Is.True);
        }

        /// <summary>
        /// CheckoutPaths가 인덱스까지 본다는 것이 이 설계의 근거다. 작업 트리만 되돌리면
        /// 외부 클라이언트가 스테이징해 둔 옛 내용이 다음 커밋에 그대로 담긴다.
        /// </summary>
        [Test]
        public void DiscardChanges_ClearsStagedContent_WhenChangeWasAlreadyStaged()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager(Server, Database, repoPath);
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "-- 잘못 추출된 내용");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "dbo/Tables/Users.sql");
            }

            git.DiscardChanges(Server, Database, new[] { "dbo/Tables/Users.sql" });

            using (var repo = new Repository(repoPath))
            {
                Assert.That(repo.RetrieveStatus("dbo/Tables/Users.sql"), Is.EqualTo(FileStatus.Unaltered));
            }
        }

        /// <summary>
        /// 화면이 넘긴 목록은 낡을 수 있다. 그 사이에 깨끗해진 파일을 HEAD로 덮어쓰면
        /// 되돌리기가 아니라 손실이다.
        /// </summary>
        [Test]
        public void DiscardChanges_LeavesFileAlone_WhenItIsAlreadyClean()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager(Server, Database, repoPath);

            var result = git.DiscardChanges(Server, Database, new[] { "dbo/Tables/Users.sql" });

            Assert.That(result.RestoredPaths, Is.Empty);
            Assert.That(result.DeletedPaths, Is.Empty);
            Assert.That(result.SkippedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
        }

        [Test]
        public void DiscardChanges_LeavesFileAlone_WhenItIsNotADbvcObjectFile()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager(Server, Database, repoPath);
            File.WriteAllText(Path.Combine(repoPath, "README.md"), "손으로 쓴 문서");

            var result = git.DiscardChanges(Server, Database, new[] { "README.md" });

            Assert.That(result.SkippedPaths, Is.EqualTo(new[] { "README.md" }));
            Assert.That(File.Exists(Path.Combine(repoPath, "README.md")), Is.True);
        }

        /// <summary>
        /// "이미 받아둔 폴더를 연결" 갈래로 커밋이 없는 저장소가 들어올 수 있다.
        /// 조용히 건너뛰면 사용자는 되돌아간 줄 안다.
        /// </summary>
        [Test]
        public void DiscardChanges_ReportsFailure_WhenRepositoryHasNoCommits()
        {
            var repoPath = NewTempDir();
            Repository.Init(repoPath);
            SeedIdentity(repoPath);
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "dbo/Tables/Users.sql");
            }
            var git = NewGitManager(Server, Database, repoPath);

            var result = git.DiscardChanges(Server, Database, new[] { "dbo/Tables/Users.sql" });

            Assert.That(result.FailedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
            Assert.That(result.RestoredPaths, Is.Empty);
        }

        [TestCase(MappingMode.Deploy)]
        [TestCase(MappingMode.Audit)]
        public void DiscardChanges_Throws_WhenModeIsNotWrite(MappingMode mode)
        {
            var repoPath = NewRepositoryWithCommit(out _, out var git, mode);
            File.WriteAllText(Path.Combine(repoPath, "seed.sql"), "-- 바뀐 내용");

            Assert.Throws<OperationNotAllowedException>(
                () => git.DiscardChanges(Server, Database, new[] { "dbo/Tables/Users.sql" }));
        }

        [Test]
        public void DiscardChanges_ReturnsEmptyResult_WhenTargetIsNotMapped()
        {
            var config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            var git = new GitManager(config);

            var result = git.DiscardChanges(Server, Database, new[] { "dbo/Tables/Users.sql" });

            Assert.That(result.RestoredPaths, Is.Empty);
            Assert.That(result.DeletedPaths, Is.Empty);
            Assert.That(result.SkippedPaths, Is.Empty);
        }
```

이 파일 위쪽에 이미 `using LibGit2Sharp;`·`using System.IO;`·`using DBVC.Core.Models;`가 있다. 없으면 더한다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerTests.DiscardChanges"`
Expected: 컴파일 실패 — `DiscardChanges`가 없다.

- [ ] **Step 3: 인터페이스에 더한다**

`src/DBVC.Core/Abstractions.cs`의 `IGitManager`에서 `CommitChanges` 선언 바로 아래:

```csharp
        /// <summary>
        /// 선택한 파일을 저장소의 마지막 커밋 내용으로 되돌린다. 추적 파일은 <c>HEAD</c>로
        /// 되돌리고, 미추적 파일은 지운다.
        ///
        /// <b>DDL 로그는 건드리지 않는다.</b> DB의 변경은 그대로 남으므로, 열린 로그 행이
        /// 가리키는 객체는 다음 새로고침의 추출로 다시 더러워진다. 그것을 화면이 문구로
        /// 알린다 — 행을 닫는 자리는 커밋 하나뿐이다.
        ///
        /// <paramref name="relativePaths"/>를 그대로 믿지 않는다. 지금의 Git 상태와
        /// 교집합만 처리한다(화면 목록은 낡을 수 있다).
        /// </summary>
        DiscardResult DiscardChanges(string serverName, string databaseName, IEnumerable<string> relativePaths);
```

- [ ] **Step 4: `GitManager`에 구현한다**

`src/DBVC.Core/GitManager.cs`의 `CommitChanges` 구현이 끝나는 자리(다음 메서드 `PullChanges` 앞)에 넣는다.

```csharp
        /// <summary>
        /// 선택한 파일을 마지막 커밋 내용으로 되돌린다. 판정은 <see cref="DiscardPlan"/>이 한다.
        /// </summary>
        public DiscardResult DiscardChanges(string serverName, string databaseName, IEnumerable<string> relativePaths)
        {
            var result = new DiscardResult();

            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return result;

            // 배포·감사 클론이 더럽다는 것은 DBVC 밖의 무언가가 만졌다는 뜻이다.
            // 남이 만든 상태를 말없이 치우지 않는다.
            var mapping = _configManager?.TryGetMapping(serverName, databaseName);
            if (mapping != null && !MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.Discard))
            {
                throw new OperationNotAllowedException(mapping.Mode, DbvcOperation.Discard);
            }

            using var repo = new Repository(repoPath);

            // 호출자가 넘긴 목록을 그대로 믿지 않는다. 화면 목록은 사용자가 들여다본 만큼
            // 낡으므로, 그 사이에 깨끗해진 파일을 HEAD로 덮어쓰면 되돌리기가 아니라 손실이다.
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in repo.RetrieveStatus(UntrackedInclusiveOptions))
            {
                var state = MapFileStatus(entry.State);
                if (state != null) states[entry.FilePath] = state;
            }

            var plan = DiscardPlan.Build(relativePaths, states);
            result.SkippedPaths.AddRange(plan.SkippedPaths);

            // 커밋이 하나도 없으면 되돌릴 기준이 없다("이미 받아둔 폴더를 연결" 갈래).
            // 조용히 건너뛰면 사용자는 되돌아간 줄 안다.
            var head = repo.Head?.Tip;

            foreach (var path in plan.RestorePaths)
            {
                if (head == null)
                {
                    result.FailedPaths.Add(path);
                    continue;
                }

                try
                {
                    // 경로 하나짜리로 한 번씩 부른다. 한 배치로 부르면 잠긴 파일 하나가
                    // 전부를 무너뜨려 실패한 경로를 가려낼 수 없다 - SSMS 편집기가 .sql을
                    // 열어 둔 잠금이 현실적인 실패다.
                    repo.CheckoutPaths("HEAD", new[] { path });
                    result.RestoredPaths.Add(path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GitManager.DiscardChanges failed to restore '{path}': {ex.Message}");
                    result.FailedPaths.Add(path);
                }
            }

            foreach (var path in plan.DeletePaths)
            {
                try
                {
                    var full = Path.GetFullPath(
                        Path.Combine(repoPath, path.Replace('/', Path.DirectorySeparatorChar)));

                    // DiscardPlan이 이미 걸렀지만 마지막 방어선은 실제 경로로 한 번 더 본다.
                    // (WorkingTreeCleaner가 같은 자리에 같은 검사를 둔 이유와 같다.)
                    if (!IsUnderRepositoryRoot(repoPath, full))
                    {
                        result.SkippedPaths.Add(path);
                        continue;
                    }

                    // 스테이징된 미추적 파일은 인덱스 항목을 먼저 내린다. 파일만 지우면
                    // 인덱스에 남은 항목이 다음 커밋에 그대로 담긴다.
                    if (head != null && repo.Index[path] != null)
                    {
                        Commands.Unstage(repo, new[] { path });
                    }

                    if (File.Exists(full)) File.Delete(full);
                    result.DeletedPaths.Add(path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GitManager.DiscardChanges failed to delete '{path}': {ex.Message}");
                    result.FailedPaths.Add(path);
                }
            }

            return result;
        }

        private static bool IsUnderRepositoryRoot(string repoPath, string candidate)
        {
            var root = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
```

`GitManager.cs` 위쪽 `using`에 `System.Collections.Generic`·`System.IO`·`System.Diagnostics`가 이미 있는지 확인하고, 없으면 더한다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerTests.DiscardChanges"`
Expected: PASS (11건)

`DiscardChanges_ClearsStagedContent_WhenChangeWasAlreadyStaged`가 실패하면 `CheckoutPaths`가 인덱스를 갱신한다는 전제가 틀린 것이다. 그때는 `repo.Index.Replace(head, new[] { path })`를 `CheckoutPaths` **앞에** 넣고, 왜 필요했는지 주석으로 남긴다.

- [ ] **Step 6: 전체 Core 테스트가 깨지지 않았는지 본다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Expected: PASS. `IGitManager`에 멤버를 더했으므로 대역 구현이 있으면 여기서 드러난다.

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 선택한 파일을 마지막 커밋 내용으로 되돌린다"
```

---

### Task 4: 재추출 없는 목록 갱신

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`Refresh`, `GatherRefresh`, `ApplyRefreshOutcome`)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Produces:
  - `private void Refresh(bool fullExtraction, bool reloadHistory = true, bool syncRepository = true)`
  - `private RefreshOutcome GatherRefresh(string server, string database, bool fullExtraction, bool includeAllAuthors, bool syncRepository, CancellationToken cancellationToken)`
  - `private string? _pendingStatusMessage` — 갱신이 끝난 뒤에 남아야 하는 한 줄

Task 5가 이것을 쓴다. 먼저 만들어 두어야 되돌리기가 자기 자신을 취소하지 않는다.

`_pendingStatusMessage`가 필요한 이유: `ApplyRefreshOutcome`이 `WarningMessage`를 무조건 덮어쓴다. 갱신을 부른 뒤에 `WarningMessage`에 대입하면, 실제 스케줄러에서는 갱신 콜백이 나중에 도착해 그 문구를 지운다. 커밋 경로의 "선택한 항목은 저장소와 이미 같아…"가 지금 그렇게 사라지고 있다 — 같은 함정을 되돌리기에 되풀이하지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`에 더한다. 리플렉션으로 private 메서드를 부른다 — Task 5가 붙기 전에 이 동작만 따로 고정하기 위해서다.

```csharp
        // ---------- 재추출 없는 갱신 ----------

        /// <summary>
        /// 되돌리기가 이 갱신을 쓴다. 재추출하면 열린 로그 행이 가리키는 객체가 다시
        /// 추출되어 같은 클릭 안에서 되돌리기가 취소된다.
        /// </summary>
        [Test]
        public void Refresh_DoesNotExtract_WhenSyncRepositoryIsFalse()
        {
            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();
            _cleaner.Invocations.Clear();

            InvokeRefresh(vm, syncRepository: false);

            _smo.Verify(s => s.ScriptObjectsDetailed(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
                It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        /// <summary>
        /// 되돌리기가 되살린 파일을 같은 갱신 안에서 cleaner가 다시 지우면, 사용자가 누른
        /// 일이 눈앞에서 취소된다. 그래서 "저장소에 아무것도 쓰지 않는다"가 규약이다.
        /// </summary>
        [Test]
        public void Refresh_DoesNotCleanWorkingTree_WhenSyncRepositoryIsFalse()
        {
            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();
            _cleaner.Invocations.Clear();

            InvokeRefresh(vm, syncRepository: false);

            _cleaner.Verify(c => c.RemoveDeletedObjectFiles(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()), Times.Never);
        }

        [Test]
        public void Refresh_StillReadsPendingChanges_WhenSyncRepositoryIsFalse()
        {
            var vm = NewConnectedViewModel();
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                new ChangeRecord
                {
                    QualifiedName = "dbo.Users", ObjectType = "TABLE", State = "Modified",
                    RelativePath = "dbo/Tables/Users.sql"
                }
            });

            InvokeRefresh(vm, syncRepository: false);

            Assert.That(vm.Changes.Count, Is.EqualTo(1));
            Assert.That(vm.Changes[0].RelativePath, Is.EqualTo("dbo/Tables/Users.sql"));
        }

        private static void InvokeRefresh(ViewChangesViewModel vm, bool syncRepository)
        {
            var method = typeof(ViewChangesViewModel).GetMethod(
                "Refresh",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(bool), typeof(bool), typeof(bool) },
                modifiers: null);
            Assert.That(method, Is.Not.Null, "Refresh(bool, bool, bool)이 없다");
            method!.Invoke(vm, new object[] { false, false, syncRepository });
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~Refresh_DoesNot"`
Expected: FAIL — "Refresh(bool, bool, bool)이 없다"

- [ ] **Step 3: 최소 구현을 쓴다**

`Refresh` 시그니처를 바꾼다:

```csharp
        /// <param name="syncRepository">
        /// 저장소를 데이터베이스에 맞출지. 거짓이면 <b>저장소에 아무것도 쓰지 않는다</b> —
        /// 추출도, 삭제된 객체의 파일 정리도 하지 않고 목록만 다시 만든다.
        ///
        /// 되돌리기가 이것을 쓴다. 참으로 두면 열린 로그 행이 가리키는 객체가 다시 추출되어
        /// 같은 클릭 안에서 되돌리기가 취소되고, 되살린 파일은 cleaner가 도로 지운다.
        /// </param>
        private void Refresh(bool fullExtraction, bool reloadHistory = true, bool syncRepository = true)
```

본문의 `_scheduler.Run` 호출에서 인자를 넘긴다:

```csharp
                () => GatherRefresh(server, database, fullExtraction, includeAllAuthors, syncRepository, token),
```

`GatherRefresh` 시그니처에 `bool syncRepository`를 `CancellationToken` 앞에 더하고, 두 자리를 감싼다:

```csharp
            // 현재 DB 상태를 파일로 추출해야 Git 상태·Diff가 최신 코드를 반영한다.
            if (syncRepository)
            {
                Extract(server, database, mapping, fullExtraction, outcome, cancellationToken);
            }
```

```csharp
            if (syncRepository && mapping != null)
            {
                var cleanup = _cleaner.RemoveDeletedObjectFiles(mapping.GitPath, outcome.Records);
                ...
            }
```

`ApplyRefreshOutcome`의 `WarningMessage` 대입을 바꾼다:

```csharp
            // 갱신이 끝난 뒤에 남아야 하는 한 줄을 여기서 꺼낸다. 갱신을 부른 쪽에서
            // WarningMessage에 대입하면 이 자리가 나중에 도착해 그것을 지운다.
            WarningMessage = outcome.Warnings.Count > 0
                ? string.Join(" / ", outcome.Warnings)
                : _pendingStatusMessage;
            _pendingStatusMessage = null;
```

필드를 다른 private 필드들 옆에 더한다:

```csharp
        /// <summary>
        /// 다음 갱신이 끝난 뒤에 보여야 하는 한 줄. ApplyRefreshOutcome이 WarningMessage를
        /// 무조건 덮어쓰므로, 갱신을 부르기 전에 여기 담아 둔다.
        /// </summary>
        private string? _pendingStatusMessage;
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"`
Expected: PASS. 기존 테스트가 하나도 깨지지 않아야 한다 — `syncRepository`의 기본값이 `true`라 기존 호출은 그대로다.

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 저장소에 쓰지 않는 목록 갱신 경로를 더한다"
```

---

### Task 5: `DiscardCommand`

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `DiscardPlan.Build` (Task 1), `DbvcOperation.Discard` (Task 2), `IGitManager.DiscardChanges` (Task 3), `Refresh(..., syncRepository:)`·`_pendingStatusMessage` (Task 4)
- Produces: `public ICommand DiscardCommand { get; }`, `internal const int MaxListedDeletePaths = 20`

`RelayCommand`는 `CommandManager.RequerySuggested`를 구독하지 않는다. 체크박스를 바꿔도 `CanExecute`가 다시 계산되지 않는데, 이는 `CommitCommand`도 지금 같은 상태다. **되돌리기에만 새 구독 장치를 붙이지 않는다** — 두 버튼이 다르게 동작하면 다음 사람이 원인을 찾는 데 시간을 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        // ---------- 되돌리기 ----------

        /// <summary>변경 하나가 선택된 상태의 뷰모델. 되돌리기 테스트가 공통으로 쓴다.</summary>
        private ViewChangesViewModel NewViewModelWithSelectedChange(
            string relativePath = "dbo/Tables/Users.sql", string state = "Modified")
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                new ChangeRecord
                {
                    QualifiedName = "dbo.Users", ObjectType = "TABLE", State = state,
                    RelativePath = relativePath
                }
            });
            _git.Setup(g => g.GetChangedFileStates(It.IsAny<string>()))
                .Returns(new Dictionary<string, string> { [relativePath] = state });
            _git.Setup(g => g.DiscardChanges(Server, Database, It.IsAny<IEnumerable<string>>()))
                .Returns(new DiscardResult());

            var vm = NewConnectedViewModel();
            vm.Changes[0].IsSelected = true;
            return vm;
        }

        [Test]
        public void Discard_DoesNotTouchRepository_WhenUserCancelsConfirmation()
        {
            var vm = NewViewModelWithSelectedChange();
            _notifier.ConfirmResult = false;

            vm.DiscardCommand.Execute(null);

            _git.Verify(g => g.DiscardChanges(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()), Times.Never);
        }

        [Test]
        public void Discard_DiscardsSelectedPaths_WhenUserConfirms()
        {
            var vm = NewViewModelWithSelectedChange();
            _notifier.ConfirmResult = true;

            vm.DiscardCommand.Execute(null);

            _git.Verify(g => g.DiscardChanges(
                Server, Database,
                It.Is<IEnumerable<string>>(p => p.Single() == "dbo/Tables/Users.sql")), Times.Once);
        }

        /// <summary>
        /// 되돌린 뒤 재추출하면 열린 로그 행이 가리키는 객체가 다시 추출되어, 같은 클릭
        /// 안에서 되돌리기가 취소된다. 이 단언이 없으면 다음 사람이 Refresh()로 되돌려
        /// 버튼은 그대로인데 눌러도 아무 일이 없어진다.
        /// </summary>
        [Test]
        public void Discard_DoesNotReExtract_AfterDiscarding()
        {
            var vm = NewViewModelWithSelectedChange();
            _notifier.ConfirmResult = true;
            _smo.Invocations.Clear();
            _cleaner.Invocations.Clear();

            vm.DiscardCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
                It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
            _cleaner.Verify(c => c.RemoveDeletedObjectFiles(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()), Times.Never);
        }

        /// <summary>복구되지 않는 쪽은 사람이 이름으로 읽어야 한다.</summary>
        [Test]
        public void Discard_NamesFilesToDelete_InTheConfirmation()
        {
            var vm = NewViewModelWithSelectedChange("dbo/Views/vSales.sql", "Added");

            vm.DiscardCommand.Execute(null);

            var message = _notifier.ConfirmCalls.Single().Message;
            Assert.That(message, Does.Contain("dbo/Views/vSales.sql"));
            Assert.That(message, Does.Contain("복구되지 않습니다"));
        }

        /// <summary>
        /// 되돌리기는 DB의 변경을 취소하지 않는다. 이 문장이 없으면 사람들이 그렇게 읽는다.
        /// </summary>
        [Test]
        public void Discard_SaysTheDatabaseChangeRemains_InTheConfirmation()
        {
            var vm = NewViewModelWithSelectedChange();

            vm.DiscardCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Single().Message,
                Does.Contain("데이터베이스의 변경은 그대로 남습니다"));
        }

        [Test]
        public void Discard_ShowsErrorBox_WhenSomePathsFail()
        {
            var vm = NewViewModelWithSelectedChange();
            _notifier.ConfirmResult = true;
            var failed = new DiscardResult();
            failed.FailedPaths.Add("dbo/Tables/Users.sql");
            _git.Setup(g => g.DiscardChanges(Server, Database, It.IsAny<IEnumerable<string>>()))
                .Returns(failed);

            vm.DiscardCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls.Any(c => c.Title.Contains("되돌리기")), Is.True);
            Assert.That(_notifier.Errors.Single(), Does.Contain("dbo/Tables/Users.sql"));
        }

        /// <summary>
        /// 갱신이 WarningMessage를 무조건 덮어쓴다. 요약을 그 뒤에 대입하면 실제
        /// 스케줄러에서는 갱신 콜백이 나중에 도착해 지워 버린다.
        /// </summary>
        [Test]
        public void Discard_KeepsSummary_AfterTheListRefreshCompletes()
        {
            var vm = NewViewModelWithSelectedChange();
            _notifier.ConfirmResult = true;
            var done = new DiscardResult();
            done.RestoredPaths.Add("dbo/Tables/Users.sql");
            _git.Setup(g => g.DiscardChanges(Server, Database, It.IsAny<IEnumerable<string>>()))
                .Returns(done);

            vm.DiscardCommand.Execute(null);

            Assert.That(vm.WarningMessage, Does.Contain("되돌림 1개"));
        }

        [Test]
        public void Discard_WarnsWithoutTouchingRepository_WhenNothingIsEligible()
        {
            var vm = NewViewModelWithSelectedChange();
            // 목록을 만든 뒤 파일이 깨끗해졌다.
            _git.Setup(g => g.GetChangedFileStates(It.IsAny<string>()))
                .Returns(new Dictionary<string, string>());

            vm.DiscardCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.EqualTo(0));
            _git.Verify(g => g.DiscardChanges(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()), Times.Never);
            Assert.That(vm.WarningMessage, Does.Contain("되돌릴 대상이 없습니다"));
        }

        [Test]
        public void DiscardCommand_CannotExecute_WhenRepositoryIsBlocked()
        {
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState
                {
                    CurrentBranch = "feature/x",
                    BlockReason = RepositoryBlockReason.BranchMismatch,
                    BlockMessage = "브랜치가 다릅니다."
                });
            var vm = NewViewModelWithSelectedChange();

            Assert.That(vm.DiscardCommand.CanExecute(null), Is.False);
        }

        /// <summary>
        /// 배포·감사 클론에서는 변경 목록 패널 자체가 뜨지 않으므로 버튼도 렌더링되지 않는다.
        /// 두 가지를 함께 단언하는 이유: CanExecute만 보면 "선택된 항목이 없어서" 거짓인
        /// 것과 구분되지 않아, 정책 게이트를 지워도 테스트가 통과한다.
        /// </summary>
        [TestCase(MappingMode.Deploy)]
        [TestCase(MappingMode.Audit)]
        public void DiscardCommand_CannotExecute_WhenModeIsNotWrite(MappingMode mode)
        {
            _config.Setup(c => c.TryGetMapping(Server, Database))
                .Returns(new MappingConfig
                {
                    ServerName = Server, DatabaseName = Database, GitPath = @"C:\repo", Mode = mode
                });
            var vm = NewConnectedViewModel();

            Assert.That(vm.ShowChangeList, Is.False, "배포·감사에는 변경 목록 패널이 없다");
            Assert.That(MappingPolicy.IsAllowed(vm.Mode, DbvcOperation.Discard), Is.False);
            Assert.That(vm.DiscardCommand.CanExecute(null), Is.False);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~Discard"`
Expected: 컴파일 실패 — `DiscardCommand`가 없다.

- [ ] **Step 3: 명령을 배선한다**

생성자에서 `CommitCommand` 줄 바로 아래:

```csharp
            DiscardCommand = new RelayCommand(Discard, CanDiscard);
```

`CommitCommand` 속성 선언 아래:

```csharp
        /// <summary>
        /// 선택한 파일을 마지막 커밋 내용으로 되돌린다. DDL 로그는 건드리지 않으므로
        /// DB의 변경은 그대로 남는다 - 확인 문구가 그 사실을 말한다.
        /// </summary>
        public ICommand DiscardCommand { get; }
```

`RaiseActionCanExecuteChanged`의 `CommitCommand` 줄 아래:

```csharp
            (DiscardCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

- [ ] **Step 4: 동작을 구현한다**

`Commit` 관련 코드 아래(`AskToCommitWithOtherAuthorsWork` 다음)에 넣는다.

```csharp
        // ---------- 되돌리기 ----------

        /// <summary>확인 문구에 이름을 적는 삭제 파일의 상한.</summary>
        internal const int MaxListedDeletePaths = 20;

        private bool CanDiscard()
        {
            // 병합 중에 경로별로 되돌리면 병합 상태가 더 헝클리고, 브랜치가 틀린 트리에서
            // 되돌리는 것은 틀린 기준으로 덮어쓰는 일이다. CanCommit과 같은 판단이다.
            if (IsBlocked) return false;

            return HasContext
                && IsMapped
                && IsInitialized
                && !IsBusy
                && Changes.Any(c => c.IsSelected)
                && MappingPolicy.IsAllowed(Mode, DbvcOperation.Discard);
        }

        private void Discard() => Discard(confirmed: false);

        /// <param name="confirmed">
        /// 사용자가 이미 확인했는지. 판정은 저장소를 여는 일이라 백그라운드에서 해야 하고
        /// 확인은 UI 스레드에서만 띄울 수 있어, 판정을 마치고 확인을 받은 뒤 이 값을 참으로
        /// 해서 같은 경로를 다시 탄다(Commit의 coAuthorConfirmed와 같은 패턴).
        ///
        /// 무한 반복 가드는 필요 없다 - 참인 경로는 대화상자를 띄우지 않으므로 세 번째
        /// 왕복이 없다.
        /// </param>
        private void Discard(bool confirmed)
        {
            if (!CanDiscard()) return;

            // 화면 객체를 읽는 일은 전부 여기서 끝낸다. 백그라운드로 넘어가는 것은 값뿐이다.
            var selectedPaths = Changes
                .Where(c => c.IsSelected)
                .Select(c => c.RelativePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();
            if (selectedPaths.Count == 0) return;

            var server = ServerName!;
            var database = DatabaseName!;
            var gitPath = _configManager.TryGetMapping(server, database)?.GitPath;
            if (gitPath == null) return;

            IsBusy = true;
            _scheduler.Run<DiscardOutcome>(
                () =>
                {
                    if (confirmed)
                    {
                        return new DiscardOutcome
                        {
                            Result = _gitManager.DiscardChanges(server, database, selectedPaths)
                        };
                    }

                    // 저장소를 여는 일이라 UI 스레드에서 하지 않는다(인코딩·신원 판정과 같은 이유).
                    var states = _gitManager.GetChangedFileStates(gitPath);
                    return new DiscardOutcome { Plan = DiscardPlan.Build(selectedPaths, states) };
                },
                outcome =>
                {
                    IsBusy = false;

                    if (outcome.Plan != null)
                    {
                        if (outcome.Plan.IsEmpty)
                        {
                            WarningMessage = "되돌릴 대상이 없습니다.";
                            return;
                        }

                        if (_notifier.Confirm("DBVC 되돌리기 확인", BuildDiscardConfirmation(outcome.Plan)))
                        {
                            Discard(confirmed: true);
                        }

                        return;
                    }

                    var result = outcome.Result!;

                    // 실패는 상자로 알린다. WarningMessage에 담으면 뒤이은 갱신의
                    // ApplyRefreshOutcome이 덮어써 사라진다.
                    if (result.HasFailures)
                    {
                        _notifier.ShowError(
                            "DBVC 되돌리기 — 일부 실패",
                            "다음 파일을 되돌리지 못했습니다. 다른 프로그램이 파일을 열고 있는지 확인하세요."
                            + Environment.NewLine + Environment.NewLine
                            + string.Join(Environment.NewLine, result.FailedPaths.Select(p => "  · " + p)));
                    }

                    // 갱신보다 먼저 담는다. ApplyRefreshOutcome이 이것을 꺼내 쓴다.
                    _pendingStatusMessage = BuildDiscardSummary(result);

                    // 재추출하지 않는다. 하면 열린 로그 행이 가리키는 객체가 다시 추출되어
                    // 같은 클릭 안에서 되돌리기가 취소된다.
                    Refresh(fullExtraction: false, reloadHistory: false, syncRepository: false);
                },
                ex =>
                {
                    IsBusy = false;
                    _notifier.ShowError("DBVC 되돌리기 실패", ex.Message);
                });
        }

        /// <summary>
        /// 되돌리기의 결과. Plan은 "아직 되돌리지 않았고 확인이 필요하다",
        /// Result는 "되돌렸다"를 뜻한다.
        /// </summary>
        private sealed class DiscardOutcome
        {
            public DiscardPlan? Plan { get; set; }
            public DiscardResult? Result { get; set; }
        }

        /// <summary>
        /// 지울 파일만 이름을 나열한다. 되돌릴 파일은 git이 갖고 있으므로 셈만으로 충분하고,
        /// 사람이 읽어야 하는 것은 복구되지 않는 쪽이다.
        /// </summary>
        private static string BuildDiscardConfirmation(DiscardPlan plan)
        {
            var nl = Environment.NewLine;
            var builder = new StringBuilder();
            builder.Append("선택한 변경을 저장소의 마지막 커밋 내용으로 되돌립니다.").Append(nl).Append(nl);

            if (plan.RestorePaths.Count > 0)
            {
                builder.Append($"  되돌릴 파일 {plan.RestorePaths.Count}개").Append(nl);
            }

            if (plan.DeletePaths.Count > 0)
            {
                builder.Append($"  지울 파일 {plan.DeletePaths.Count}개 — 복구되지 않습니다").Append(nl);
                foreach (var path in plan.DeletePaths.Take(MaxListedDeletePaths))
                {
                    builder.Append("    · ").Append(path).Append(nl);
                }

                // 전체 다시 추출 뒤라면 수백 개가 될 수 있다. 화면 밖으로 넘친 목록은
                // 확인이 아니라 장애물이다.
                var rest = plan.DeletePaths.Count - MaxListedDeletePaths;
                if (rest > 0) builder.Append($"    외 {rest}개").Append(nl);
            }

            builder.Append(nl)
                .Append("데이터베이스의 변경은 그대로 남습니다. 다음 새로고침에서 다시 추출됩니다.")
                .Append(nl).Append(nl)
                .Append("계속할까요?");

            return builder.ToString();
        }

        private static string BuildDiscardSummary(DiscardResult result)
        {
            var parts = new List<string>();
            if (result.RestoredPaths.Count > 0) parts.Add($"되돌림 {result.RestoredPaths.Count}개");
            if (result.DeletedPaths.Count > 0) parts.Add($"삭제 {result.DeletedPaths.Count}개");
            // 사용자가 체크한 것이 조용히 빠지면 안 된다.
            if (result.SkippedPaths.Count > 0) parts.Add($"제외 {result.SkippedPaths.Count}개");
            if (result.FailedPaths.Count > 0) parts.Add($"실패 {result.FailedPaths.Count}개");

            return parts.Count == 0
                ? "되돌릴 대상이 없습니다."
                : "되돌렸습니다 — " + string.Join(", ", parts) + ".";
        }
```

`ViewChangesViewModel.cs` 위쪽 `using`에 `System.Text`가 없으면 더한다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~Discard"`
Expected: PASS (12건)

- [ ] **Step 6: 전체 테스트를 돌린다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: PASS

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 변경 목록에 되돌리기를 더한다"
```

---

### Task 6: 버튼을 화면에 놓는다

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml:190` 부근

**Interfaces:**
- Consumes: `DiscardCommand` (Task 5)

`.xaml`은 CI가 렌더링을 검증하지 못한다. 여기서는 빌드가 깨지지 않는지만 보고, 실제 확인은 Task 7의 수동 검증 절로 넘긴다.

- [ ] **Step 1: 버튼을 더한다**

`Content="Commit"` 버튼 바로 아래에 넣는다. `Pull`·`Push`가 git 용어를 그대로 쓰는 것과 달리 되돌리기는 한국어다 — `새로고침`·`전체 다시 추출`·`원격 확인`과 같은 갈래다.

```xml
                    <Button Content="되돌리기" Command="{Binding DiscardCommand}" Width="70" Margin="0,0,10,4"
                            ToolTip="선택한 파일을 저장소의 마지막 커밋 내용으로 되돌립니다. 데이터베이스의 변경은 그대로 남습니다." />
```

- [ ] **Step 2: 빌드를 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공. XAML 바인딩 오타는 컴파일에서 잡히지 않으므로 이름을 눈으로 한 번 더 대조한다 — `DiscardCommand`.

- [ ] **Step 3: 커밋**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml
git commit -m "feat(vsix): 변경 목록에 되돌리기 버튼을 놓는다"
```

---

### Task 7: 문서와 버전

**Files:**
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest:4` (`Version="0.5.17"` → `"0.5.18"`)
- Modify: `README.md`
- Modify: `docs/setup-checklist.md`
- Modify: `docs/team-rollout-backlog.md`

버전은 매니페스트 한 곳에만 적는다 — `DbvcVersion`이 빌드 시 그 값을 읽으므로 코드에 숫자를 넣으면 두 곳이 어긋난다.

- [ ] **Step 1: 매니페스트 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest`의 `<Identity ... Version="0.5.17" ...>`를 `0.5.18`로 바꾼다.

- [ ] **Step 2: `README.md`에 동작을 적는다**

커밋을 설명하는 절 옆에 더한다. 되돌리기가 DB를 건드리지 않는다는 것이 핵심이다.

```markdown
### 작업 트리 되돌리기

변경 목록에서 항목을 고르고 **되돌리기**를 누르면 그 `.sql`이 저장소의 마지막 커밋 내용으로
돌아간다. 새로 추출된 파일(상태 `추가`)은 되돌릴 내용이 없으므로 지워지며, 이쪽은 복구되지
않는다 — 확인 대화상자가 지울 파일의 이름을 먼저 보여준다.

**데이터베이스의 변경은 그대로 남는다.** 되돌리기는 파일만 되돌리고 `DBVC_ChangeLog`는
건드리지 않으므로, 아직 커밋하지 않은 객체는 다음 새로고침에서 다시 추출된다. 목록에서
완전히 치우려면 되돌린 뒤 그 항목을 커밋한다 — 저장소와 이미 같으므로 커밋은 만들어지지 않고
변경 로그만 정리된다.

배포·감사 용도로 연결한 저장소에는 이 기능이 없다. 그쪽은 변경 목록 화면 자체가 뜨지 않는다.
```

- [ ] **Step 3: `docs/setup-checklist.md`에 수동 검증 절을 더한다**

"알아 둘 것" 목록 근처, 다른 수동 확인 절차와 같은 자리에 넣는다.

```markdown
### 되돌리기 수동 확인 (0.5.18)

CI는 대화상자 렌더링·버튼 배치·파일 잠금을 검증하지 못한다. SSMS 21에서 직접 확인한다.

1. 객체를 하나 바꾸고 새로고침 → 목록에 뜬다 → 체크하고 **되돌리기** → 확인 문구에
   "되돌릴 파일 1개"가 보인다 → 파일이 저장소 내용으로 돌아온다
2. **그 항목은 목록에 그대로 남아 있다.** 새로고침을 누르면 다시 더러워진다 —
   되돌리기는 DB의 변경을 취소하지 않는다는 설계가 눈에 보이는 자리다
3. 새 객체를 만들고 새로고침 → 상태 `추가` 항목 → **되돌리기** → 확인 문구에 파일 이름과
   "복구되지 않습니다"가 보인다 → 파일이 사라진다
4. 되돌릴 `.sql`을 편집기로 열어 둔 채 **되돌리기** → 실패 상자가 뜨고 나머지 파일은 처리된다
5. 배포 용도로 연결한 저장소에는 되돌리기 버튼이 보이지 않는다(변경 목록 패널 자체가 없다)
```

"막혔을 때" 표에도 한 줄 더한다:

```markdown
| 되돌렸는데 새로고침하면 다시 나타난다 | 정상이다. 되돌리기는 파일만 되돌리고 데이터베이스의 변경은 남긴다. 목록에서 치우려면 되돌린 뒤 그 항목을 커밋한다 |
```

- [ ] **Step 4: `docs/team-rollout-backlog.md`를 고친다**

머리말의 완료 목록에 `0.5.18`을 더하고, 2번 항목을 이렇게 바꾼다.

```markdown
### 2. 작업 트리 되돌리기(discard) — 0.5.18로 끝남

변경 목록에서 고른 파일을 마지막 커밋 내용으로 되돌린다.
설계는 `docs/superpowers/specs/2026-09-07-dbvc-discard-changes-design.md`.

**파일까지만 되돌린다.** `DBVC_ChangeLog`의 행은 건드리지 않으므로, 아직 커밋하지 않은
객체는 다음 새로고침에서 다시 추출된다. 행을 닫는 자리는 커밋 하나로 유지했다 — 공용 DB
하나를 23명이 쓰는 환경에서 행을 닫는 것은 전역이고, 닫힌 행이 가리키던 DB의 변경은
git에 영영 담기지 않는다. 그 동작("무시")은 아래 4·5번과 한 몸으로 설계한다.
```

그리고 새 항목을 8번으로 세운다:

```markdown
### 8. 차단된 배포·감사 클론의 막다른 길

배포·감사 클론은 작업 트리가 더러우면 `WorkingTreeDirty`로 차단되는데, 그 화면에는 변경
목록이 없어(`PanelSelector`) 2번의 되돌리기가 닿지 않는다. 도구 안에서 빠져나갈 방법이
하나도 없고 외부 git 클라이언트가 필요하다.

DBVC가 그 클론을 더럽힐 수는 없으므로(추출 금지, 차이 검사는 아무것도 쓰지 않는다) 원인은
항상 도구 밖에 있다. 그래서 자동으로 치우지 않는다 — 차단 배너 위에 사유와 함께 뜨는
별도 버튼이 필요하다. 쓰는 사람이 DBA 3명뿐이라 우선순위는 낮다.
```

- [ ] **Step 5: 전체 빌드와 테스트를 돌린다**

```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests
dotnet test tests/DBVC.Vsix.Tests
```
Expected: 전부 PASS. `DbvcVersionTests`가 매니페스트 버전을 읽으므로 여기서 함께 확인된다.

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/source.extension.vsixmanifest README.md docs/setup-checklist.md docs/team-rollout-backlog.md
git commit -m "docs: 되돌리기를 문서에 적고 0.5.18로 올린다"
```

- [ ] **Step 7: `.vsix`가 실제로 만들어지는지 본다**

```bash
dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release
ls src/DBVC.Vsix/bin/Release/net48/*.vsix
```

빌드 성공이 `.vsix` 생성을 뜻하지 않는다. 산출물이 없으면 `Microsoft.VSSDK.BuildTools`가 복원·임포트되지 않은 것이므로 개발자 셸에서 `msbuild src/DBVC.Vsix/DBVC.Vsix.csproj -restore -p:Configuration=Release`로 한 번 더 확인한다.

- [ ] **Step 8: 수동 검증은 아직 끝나지 않았다**

Step 3의 다섯 항목은 SSMS 21에서 직접 눌러야 한다. **그 전에는 "동작한다"고 말하지 않는다.** 특히 4번(잠긴 `.sql`)은 실제 편집기 잠금이 있어야 재현된다.

---

## 참고 — 이 계획이 손대지 않는 것

- `DBVC_ChangeLog`와 `MarkProcessed`. 되돌리기는 DB에 아무것도 쓰지 않는다
- `ExtractionBaseline`. `DiscardPlan`의 경로 규약 검사가 기준선 표식을 보호한다
- `WorkingTreeCleaner`. 근거가 DDL 로그인 정리와 근거가 Git 상태인 되돌리기를 한 클래스에 두지 않는다
- `RelayCommand`의 재조회 방식. `CommitCommand`와 같은 상태로 둔다
- 패키지 버전과 `DBVC.Vsix.csproj`의 VSIX 배선
