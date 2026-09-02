# 이력 뷰 정합성·Git 의미론·스레드 정리 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 이력 뷰의 단일 객체 모드를 플래그 하나로 통합하고, 커밋 조회를 Core API 하나로 합쳐 백그라운드로 돌리며, 이름 변경·병합 커밋이 화면에 사실대로 보이게 한다.

**Architecture:** `IGitManager`의 커밋 조회 세 메서드를 `GetCommitDetail` 하나로 합쳐 저장소를 한 번만 연다. `ObjectHistoryViewModel`이 `IBackgroundScheduler`를 받아 UI 스레드를 비우고 stale 가드로 순서를 지킨다. `ViewChangesViewModel.IsSingleObjectMode`를 없애 모드의 진실을 `ObjectHistoryViewModel.RelativePath` 하나로 만들고, 전용 전체화면 XAML 블록과 복제된 AvalonEdit 렌더러 두 쌍을 제거한다.

**Tech Stack:** C# 8.0, .NET Framework 4.8 / netstandard2.0, WPF (MVVM), LibGit2Sharp 0.32.0, AvalonEdit, DiffPlex, NUnit, Moq

**Spec:** `docs/superpowers/specs/2026-09-02-dbvc-history-view-consolidation-design.md`

## Global Constraints

- 사용자에게 보이는 모든 문구는 한국어다. 예외 메시지, 알림, 버튼, ToolTip, 컬럼명 포함.
- 주석은 "왜"만 적는다. 한국어 평서문으로, 함정과 근거를 남기는 기존 문체를 따른다.
- 커밋 메시지는 한국어 명령형 현재시제 + 스코프: `feat(core): 메모리 전용 자격증명 저장소를 더한다`.
- TDD: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋. 테스트 이름은 영어 `Method_Result_WhenCondition` 형태다.
- 저장소 경로는 `ObjectPathConvention` 한 곳에서만 정한다. 구분자는 항상 `/`.
- 패키지 버전을 올리지 않는다. `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0`, `LibGit2Sharp 0.32.0` 고정.
- 테스트 프로젝트에 MDS/SMO를 직접 `PackageReference` 하지 않는다. 전이 참조로만 받는다.
- 모든 커밋에서 솔루션이 빌드되어야 한다. Task 1이 새 API를 더하고 Task 3이 호출부를 옮긴 뒤 Task 4가 구 API를 지우는 순서가 그 때문이다.

**빌드·테스트 명령**

```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests -f net48
```

`DBVC.Vsix.Tests`는 net48에서만 돈다(WPF/VSSDK 참조). `DBVC.Core.Tests`는 net48·net10.0 멀티타깃이며 빠른 반복에는 net10.0을 쓴다.

---

### Task 1: Core — `CommitDetail`과 `GetCommitDetail`

저장소를 한 번만 여는 통합 조회를 더한다. 기존 세 메서드는 아직 지우지 않는다 — 호출부가 Task 3에서 옮겨간 뒤 Task 4에서 지운다.

**Files:**
- Modify: `src/DBVC.Core/HistoryChangedFile.cs`
- Modify: `src/DBVC.Core/Abstractions.cs:125`
- Modify: `src/DBVC.Core/GitManager.cs` (`GetChangedFilesAtCommit` 아래, `ReadBlobText` 위)
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Produces: `CommitDetail GetCommitDetail(string serverName, string databaseName, string commitSha, string? relativeFilePath)`
- Produces: `public const int GitManager.MaxChangedFilesPerCommit = 500`
- Produces: `CommitDetail { bool IsTruncated; int TotalChangedFileCount; IReadOnlyList<HistoryChangedFile> ChangedFiles; string? OldText; string? NewText; }`

- [ ] **Step 1: Write the failing tests**

`tests/DBVC.Core.Tests/GitManagerTests.cs`의 `// ---------- GetChangedFilesAtCommit ----------` 블록 **위**에 아래를 넣는다. 파일 상단 `using` 목록에는 이미 `System.Linq`, `LibGit2Sharp`, `DBVC.Core`가 있으므로 추가할 것이 없다.

```csharp
        // ---------- GetCommitDetail ----------

        [Test]
        public void GetCommitDetail_ReturnsOldAndNewText_WhenRelativeFilePathIsGiven()
        {
            var repoPath = NewRepoWithCommit("dbo/Tables/Users.sql", "V1");
            string sha1, sha2;
            using (var repo = new Repository(repoPath))
            {
                sha1 = repo.Head.Tip.Sha;
            }

            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "V2");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                sha2 = repo.Commit("update", TestSignature, TestSignature).Sha;
            }

            var git = NewGitManager(Server, Database, repoPath);

            var second = git.GetCommitDetail(Server, Database, sha2, "dbo/Tables/Users.sql");
            Assert.That(second.OldText, Is.EqualTo("V1"));
            Assert.That(second.NewText, Is.EqualTo("V2"));
            Assert.That(second.ChangedFiles, Is.Empty, "경로가 주어지면 목록은 채우지 않는다");

            // 최초 커밋은 부모가 없으므로 이전 내용이 빈 문자열이다(null이 아니다).
            var first = git.GetCommitDetail(Server, Database, sha1, "dbo/Tables/Users.sql");
            Assert.That(first.OldText, Is.EqualTo(string.Empty));
            Assert.That(first.NewText, Is.EqualTo("V1"));
        }

        [Test]
        public void GetCommitDetail_ReturnsChangedFiles_WhenRelativeFilePathIsNull()
        {
            var repoPath = NewRepoWithCommit("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);");
            string sha1, sha2;
            using (var repo = new Repository(repoPath))
            {
                sha1 = repo.Head.Tip.Sha;
            }

            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                sha2 = repo.Commit("update and add", TestSignature, TestSignature).Sha;
            }

            var git = NewGitManager(Server, Database, repoPath);

            var initial = git.GetCommitDetail(Server, Database, sha1, null);
            Assert.That(initial.ChangedFiles.Count, Is.EqualTo(1));
            Assert.That(initial.ChangedFiles[0].State, Is.EqualTo(HistoryChangedFileState.Added));
            Assert.That(initial.TotalChangedFileCount, Is.EqualTo(1));
            Assert.That(initial.IsTruncated, Is.False);
            Assert.That(initial.OldText, Is.Null, "경로가 없으면 본문은 채우지 않는다");

            var second = git.GetCommitDetail(Server, Database, sha2, null);
            Assert.That(second.ChangedFiles.Count, Is.EqualTo(2));
            var users = second.ChangedFiles.First(c => c.RelativePath == "dbo/Tables/Users.sql");
            var orders = second.ChangedFiles.First(c => c.RelativePath == "dbo/Tables/Orders.sql");
            Assert.That(users.State, Is.EqualTo(HistoryChangedFileState.Modified));
            Assert.That(orders.State, Is.EqualTo(HistoryChangedFileState.Added));
        }

        [Test]
        public void GetCommitDetail_ReportsRenameAsDeleteAndAdd_WhenContentIsUnchanged()
        {
            // DBVC에서 객체 이름 변경은 파일 삭제 + 생성이다. rename 검출이 켜져 있으면
            // 새 경로 한 행으로 뭉쳐지고, 그 경로가 부모 트리에 없어 Diff가 전체 추가로 뜬다.
            const string body = "CREATE PROCEDURE usp_Old AS SELECT 1;";
            var repoPath = NewRepoWithCommit("dbo/StoredProcedures/usp_Old.sql", body);

            string renameSha;
            using (var repo = new Repository(repoPath))
            {
                File.Delete(Path.Combine(repoPath, "dbo", "StoredProcedures", "usp_Old.sql"));
                WriteRepoFile(repoPath, "dbo/StoredProcedures/usp_New.sql", body);
                Commands.Stage(repo, "*");
                renameSha = repo.Commit("rename", TestSignature, TestSignature).Sha;
            }

            var git = NewGitManager(Server, Database, repoPath);
            var detail = git.GetCommitDetail(Server, Database, renameSha, null);

            Assert.That(detail.ChangedFiles.Count, Is.EqualTo(2));
            var deleted = detail.ChangedFiles.First(c => c.RelativePath == "dbo/StoredProcedures/usp_Old.sql");
            var added = detail.ChangedFiles.First(c => c.RelativePath == "dbo/StoredProcedures/usp_New.sql");
            Assert.That(deleted.State, Is.EqualTo(HistoryChangedFileState.Deleted));
            Assert.That(added.State, Is.EqualTo(HistoryChangedFileState.Added));
        }

        [Test]
        public void GetCommitDetail_TruncatesTheFileList_WhenTheCommitExceedsTheLimit()
        {
            var repoPath = NewTempDir();
            Repository.Init(repoPath);

            var total = GitManager.MaxChangedFilesPerCommit + 3;
            for (int i = 0; i < total; i++)
            {
                WriteRepoFile(repoPath, $"dbo/Tables/T{i}.sql", $"CREATE TABLE T{i} (Id INT);");
            }

            string sha;
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                sha = repo.Commit("baseline", TestSignature, TestSignature).Sha;
            }

            var git = NewGitManager(Server, Database, repoPath);
            var detail = git.GetCommitDetail(Server, Database, sha, null);

            Assert.That(detail.ChangedFiles.Count, Is.EqualTo(GitManager.MaxChangedFilesPerCommit));
            Assert.That(detail.TotalChangedFileCount, Is.EqualTo(total));
            Assert.That(detail.IsTruncated, Is.True);
        }

        [Test]
        public void GetCommitDetail_ReturnsEmpty_WhenCommitOrMappingIsMissing()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager(Server, Database, repoPath);

            using var repo = new Repository(repoPath);
            var headSha = repo.Head.Tip.Sha;

            Assert.That(git.GetCommitDetail(Server, Database, "0000000000000000000000000000000000000000", null).ChangedFiles, Is.Empty);
            Assert.That(git.GetCommitDetail(Server, "unmapped", headSha, null).ChangedFiles, Is.Empty);
            Assert.That(git.GetCommitDetail(Server, Database, "", null).ChangedFiles, Is.Empty);
            Assert.That(git.GetCommitDetail(Server, Database, headSha, "dbo/Tables/NoSuch.sql").NewText, Is.Null);
        }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GetCommitDetail"`
Expected: FAIL — `CommitDetail`, `GetCommitDetail`, `MaxChangedFilesPerCommit`이 없어 컴파일 오류.

- [ ] **Step 3: Add the `CommitDetail` model**

`src/DBVC.Core/HistoryChangedFile.cs` 끝의 `HistoryChangedFile` 클래스 **아래**, `}` 안쪽에 더한다. 파일 상단에 `using System.Collections.Generic;`을 넣는다.

```csharp
    /// <summary>
    /// 커밋 하나를 화면에 그리는 데 필요한 정보를 한 번의 저장소 열기로 모아 담는다.
    ///
    /// 목록과 본문을 한 타입에 둔 이유는 호출 횟수다. 나누면 커밋을 고를 때마다
    /// Repository를 두세 번 열게 되고, 그 비용이 UI 스레드에서 난다.
    /// </summary>
    public class CommitDetail
    {
        /// <summary>표시 상한을 넘어 <see cref="ChangedFiles"/>가 잘렸다.</summary>
        public bool IsTruncated { get; set; }

        /// <summary>자르기 전의 전체 변경 파일 수. 안내 문구가 이 값을 쓴다.</summary>
        public int TotalChangedFileCount { get; set; }

        public IReadOnlyList<HistoryChangedFile> ChangedFiles { get; set; } = new List<HistoryChangedFile>();

        /// <summary>
        /// 부모 커밋 시점의 파일 내용. 부모가 없는 최초 커밋이면 빈 문자열이다.
        /// 조회 경로를 주지 않았거나 그 트리에 파일이 없으면 <c>null</c>이다.
        /// </summary>
        public string? OldText { get; set; }

        /// <summary>이 커밋 시점의 파일 내용. 삭제된 파일이거나 조회 경로를 주지 않았으면 <c>null</c>이다.</summary>
        public string? NewText { get; set; }
    }
```

- [ ] **Step 4: Declare it on `IGitManager`**

`src/DBVC.Core/Abstractions.cs`의 `GetChangedFilesAtCommit` 선언(`:125`) 아래에 더한다.

```csharp
        /// <summary>
        /// 커밋 하나의 정보를 한 번의 저장소 열기로 읽는다.
        /// <paramref name="relativeFilePath"/>가 비면 변경 파일 목록만, 주어지면 그 파일의 이전·이후 본문만 채운다.
        /// </summary>
        CommitDetail GetCommitDetail(string serverName, string databaseName, string commitSha, string? relativeFilePath);
```

- [ ] **Step 5: Implement it in `GitManager`**

`src/DBVC.Core/GitManager.cs`의 `GetChangedFilesAtCommit` 메서드 **바로 아래**(`private static string? ReadBlobText` 위)에 더한다.

```csharp
        /// <summary>
        /// 변경 파일 목록을 이 개수까지만 채운다. 기준선이 없어 처음 도는 전체 추출은
        /// 수천 개 파일을 한 커밋에 담는데, 그것을 전부 ObservableCollection에 옮기면
        /// 화면이 멈춘다. 잘렸다는 사실은 CommitDetail.IsTruncated로 알린다.
        /// </summary>
        public const int MaxChangedFilesPerCommit = 500;

        /// <summary>
        /// 커밋 하나의 정보를 한 번의 저장소 열기로 읽는다. (설계 5.1)
        /// 목록과 본문을 나눠 부르면 커밋을 고를 때마다 Repository를 두세 번 열게 된다.
        /// </summary>
        public CommitDetail GetCommitDetail(string serverName, string databaseName, string commitSha, string? relativeFilePath)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || string.IsNullOrWhiteSpace(commitSha)) return new CommitDetail();

            try
            {
                using var repo = new Repository(repoPath);
                var commit = repo.Lookup<Commit>(commitSha);
                if (commit == null) return new CommitDetail();

                var parent = commit.Parents.FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(relativeFilePath))
                {
                    var path = NormalizePath(relativeFilePath!);
                    return new CommitDetail
                    {
                        // 부모가 없으면 되돌아갈 상태가 없다. null(조회 실패)과 구분해 빈 문자열을 준다.
                        OldText = parent == null ? string.Empty : ReadBlobText(parent, path),
                        NewText = ReadBlobText(commit, path)
                    };
                }

                // rename 검출을 끈다. DBVC에서 객체 이름 변경은 옛 .sql 삭제 + 새 .sql 생성이고
                // 내용이 거의 같아 기본값(SimilarityOptions.Default)이면 새 경로 한 행으로 뭉쳐진다.
                // 그 경로는 부모 트리에 없으므로 Diff가 파일 전체 추가로 뜬다 - 사실과 다르다.
                var options = new CompareOptions { Similarity = SimilarityOptions.None };
                using var changes = repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree, options);

                var all = changes.ToList();
                var files = all
                    .Take(MaxChangedFilesPerCommit)
                    .Select(c => new HistoryChangedFile
                    {
                        State = c.Status == ChangeKind.Added ? HistoryChangedFileState.Added :
                                c.Status == ChangeKind.Deleted ? HistoryChangedFileState.Deleted :
                                HistoryChangedFileState.Modified,
                        RelativePath = c.Path
                    })
                    .ToList();

                return new CommitDetail
                {
                    ChangedFiles = files,
                    TotalChangedFileCount = all.Count,
                    IsTruncated = all.Count > MaxChangedFilesPerCommit
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetCommitDetail failed for '{commitSha}': {ex.Message}");
                return new CommitDetail();
            }
        }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GetCommitDetail"`
Expected: PASS (5 tests)

- [ ] **Step 7: Verify nothing else broke**

Run: `dotnet build DBVC.slnx`
Expected: 0 Errors. 구 메서드는 아직 남아 있으므로 호출부가 깨지지 않는다.

- [ ] **Step 8: Commit**

```bash
git add src/DBVC.Core/HistoryChangedFile.cs src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 커밋 정보를 한 번에 읽는 GetCommitDetail을 더한다"
```

---

### Task 2: Core — `CommitInfo.ParentCount`

병합 커밋임을 화면이 알 수 있게 부모 수를 실어 보낸다. 이력 목록을 그릴 때 이미 읽는 값이라 추가 조회가 없다.

**Files:**
- Modify: `src/DBVC.Core/Models/CommitInfo.cs`
- Modify: `src/DBVC.Core/GitManager.cs` (`GetHistory`의 `Select`)
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Produces: `int CommitInfo.ParentCount`

- [ ] **Step 1: Write the failing test**

`tests/DBVC.Core.Tests/GitManagerTests.cs`의 `// ---------- GetCommitDetail ----------` 블록 위에 더한다.

```csharp
        [Test]
        public void GetHistory_SetsParentCountToTwo_ForAMergeCommit()
        {
            var repoPath = NewRepoWithCommit("dbo/Tables/Users.sql", "V1");

            using (var repo = new Repository(repoPath))
            {
                var baseCommit = repo.Head.Tip;

                // 갈래를 하나 만들어 서로 다른 파일을 커밋한 뒤 병합한다.
                var side = repo.CreateBranch("side", baseCommit);
                Commands.Checkout(repo, side);
                WriteRepoFile(repoPath, "dbo/Tables/Side.sql", "CREATE TABLE Side (Id INT);");
                Commands.Stage(repo, "*");
                repo.Commit("side work", TestSignature, TestSignature);

                Commands.Checkout(repo, "master");
                WriteRepoFile(repoPath, "dbo/Tables/Main.sql", "CREATE TABLE Main (Id INT);");
                Commands.Stage(repo, "*");
                repo.Commit("main work", TestSignature, TestSignature);

                repo.Merge(side, TestSignature, new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
            }

            var git = NewGitManager(Server, Database, repoPath);
            var history = git.GetHistory(Server, Database, null);

            Assert.That(history[0].ParentCount, Is.EqualTo(2), "가장 최근 커밋이 병합 커밋이다");
            Assert.That(history.Skip(1).All(c => c.ParentCount <= 1), Is.True);
        }
```

> `Repository.Init`의 기본 브랜치 이름은 libgit2 설정을 따르며 이 저장소의 다른 테스트도 그 기본값 위에서 돈다. `Commands.Checkout(repo, "master")`가 실패하면 `repo.Head.FriendlyName`을 갈래 생성 전에 변수로 잡아 그 이름으로 되돌아온다.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~SetsParentCountToTwo"`
Expected: FAIL — `CommitInfo`에 `ParentCount`가 없어 컴파일 오류.

- [ ] **Step 3: Add the property**

`src/DBVC.Core/Models/CommitInfo.cs`의 `ParentSha` 아래에 더한다.

```csharp
        /// <summary>
        /// 부모 커밋 수. 2 이상이면 병합 커밋이다.
        /// 화면이 이 값으로 병합 표시를 내는데, 파일 목록과 Diff는 첫 부모 기준이라
        /// 표시가 없으면 사용자가 상대 브랜치에서 들어온 변경을 이 커밋이 만든 것으로 읽는다.
        /// </summary>
        public int ParentCount { get; set; }
```

- [ ] **Step 4: Fill it in `GetHistory`**

`src/DBVC.Core/GitManager.cs`의 `GetHistory` 안 `Select(commit => new CommitInfo { ... })`에서 `ParentSha` 줄 아래에 더한다.

```csharp
                        ParentCount = commit.Parents.Count(),
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerTests"`
Expected: PASS (기존 테스트 포함 전부)

- [ ] **Step 6: Commit**

```bash
git add src/DBVC.Core/Models/CommitInfo.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 커밋 이력에 부모 수를 실어 병합 커밋을 구분한다"
```

---

### Task 3: Vsix — `ObjectHistoryViewModel`을 백그라운드로 옮긴다

`GetCommitDetail`로 호출을 바꾸고, `IBackgroundScheduler`로 UI 스레드를 비우고, 겹친 요청의 순서를 stale 가드로 지킨다. 병합·상한 안내도 여기서 만든다.

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:98`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitManager.GetCommitDetail` (Task 1), `CommitInfo.ParentCount` (Task 2)
- Produces: `ObjectHistoryViewModel(IGitManager gitManager, DiffService diffService, IBackgroundScheduler scheduler)`
- Produces: `string? ObjectHistoryViewModel.ChangedFilesNotice`, `bool ObjectHistoryViewModel.HasChangedFilesNotice`
- Produces: `int HistoryEntryViewModel.ParentCount`, `string HistoryEntryViewModel.MergeMark`

- [ ] **Step 1: Add the deferrable scheduler test double**

`tests/DBVC.Vsix.Tests/ViewModels/TestDoubles.cs` 끝의 네임스페이스 안에 더한다. 파일 상단 `using`에 `System`, `System.Collections.Generic`, `System.Linq`, `DBVC.Vsix.Services`가 없으면 넣는다.

```csharp
    /// <summary>
    /// 작업은 즉시 돌리되 결과 반영은 큐에 담아 둔다. 테스트가 순서를 골라 흘려보내
    /// "늦게 끝난 앞선 요청"을 흉내 낼 수 있다 - stale 가드는 그 상황에서만 드러난다.
    /// </summary>
    public sealed class DeferredBackgroundScheduler : IBackgroundScheduler
    {
        private readonly List<Action> _pending = new List<Action>();

        public int PendingCount => _pending.Count;

        public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
        {
            T value;
            try
            {
                value = work();
            }
            catch (Exception ex)
            {
                _pending.Add(() => onFailed(ex));
                return;
            }

            _pending.Add(() => onSucceeded(value));
        }

        public void Post(Action action) => action();

        /// <summary>담긴 순서대로 모두 흘린다.</summary>
        public void FlushAll()
        {
            var snapshot = _pending.ToList();
            _pending.Clear();
            foreach (var callback in snapshot) callback();
        }

        /// <summary><paramref name="index"/>번째 콜백만 흘린다.</summary>
        public void FlushAt(int index)
        {
            var callback = _pending[index];
            _pending.RemoveAt(index);
            callback();
        }
    }
```

- [ ] **Step 2: Write the failing tests**

`tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs`에 더한다. 파일 상단 `using`에 `using DBVC.Vsix.Services;`를 넣는다(`DiffService`, `IBackgroundScheduler` 때문). 그리고 클래스 안 헬퍼 옆에 아래 두 헬퍼를 더한다.

```csharp
        private static HistoryChangedFile ChangedFile(string relativePath, HistoryChangedFileState state = HistoryChangedFileState.Modified)
            => new HistoryChangedFile { RelativePath = relativePath, State = state };

        private static CommitDetail Detail(params HistoryChangedFile[] files)
            => new CommitDetail { ChangedFiles = files.ToList(), TotalChangedFileCount = files.Length };
```

이어서 테스트를 더한다.

```csharp
        [Test]
        public void SelectedEntry_IgnoresTheEarlierRequest_WhenItFinishesLast()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(Detail(ChangedFile("dbo/Tables/A.sql")));
            _git.Setup(g => g.GetCommitDetail(Server, Database, "bbb", null))
                .Returns(Detail(ChangedFile("dbo/Tables/B.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };
            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "bbb", ShortSha = "bbb" };

            // 나중 요청(bbb)을 먼저, 앞선 요청(aaa)을 나중에 흘린다.
            scheduler.FlushAt(1);
            scheduler.FlushAt(0);

            Assert.That(vm.ChangedFiles.Count, Is.EqualTo(1));
            Assert.That(vm.ChangedFiles[0].RelativePath, Is.EqualTo("dbo/Tables/B.sql"));
        }

        [Test]
        public void SelectedEntry_RunsTheGitReadThroughTheScheduler()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(Detail(ChangedFile("dbo/Tables/A.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };

            Assert.That(vm.ChangedFiles, Is.Empty, "콜백을 흘리기 전에는 컬렉션이 비어 있어야 한다");
            Assert.That(scheduler.PendingCount, Is.EqualTo(1));

            scheduler.FlushAll();
            Assert.That(vm.ChangedFiles.Count, Is.EqualTo(1));
        }

        [Test]
        public void SelectedEntry_BuildsAMergeNotice_WhenTheCommitHasTwoParents()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(Detail(ChangedFile("dbo/Tables/A.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa", ParentCount = 2 };

            Assert.That(vm.HasChangedFilesNotice, Is.True);
            Assert.That(vm.ChangedFilesNotice, Does.Contain("병합 커밋"));
        }

        [Test]
        public void SelectedEntry_BuildsATruncationNotice_WhenTheFileListIsCut()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(new CommitDetail
                {
                    ChangedFiles = new List<HistoryChangedFile> { ChangedFile("dbo/Tables/A.sql") },
                    TotalChangedFileCount = 900,
                    IsTruncated = true
                });

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };

            Assert.That(vm.ChangedFilesNotice, Does.Contain("900"));
        }

        [Test]
        public void HistoryEntryViewModel_MergeMark_IsSetOnlyWhenParentCountExceedsOne()
        {
            var merge = HistoryEntryViewModel.From(new CommitInfo { Sha = "a", ParentCount = 2 });
            var plain = HistoryEntryViewModel.From(new CommitInfo { Sha = "b", ParentCount = 1 });

            Assert.That(merge.MergeMark, Is.EqualTo("병합"));
            Assert.That(plain.MergeMark, Is.Empty);
        }
```

기존 테스트 중 구 API를 모의하는 것들을 새 API로 바꾼다. `_git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, X)).Returns("old")`와 짝이 되는 `GetFileContentAtCommit(... ).Returns("new")` 두 줄을 다음 한 줄로 바꾼다.

```csharp
            _git.Setup(g => g.GetCommitDetail(Server, Database, X, RelativePath))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });
```

`_git.Setup(g => g.GetChangedFilesAtCommit(Server, Database, commitSha)).Returns(changed)`는 다음으로 바꾼다.

```csharp
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(new CommitDetail { ChangedFiles = changed, TotalChangedFileCount = changed.Count });
```

`_git.Verify(g => g.GetChangedFilesAtCommit(...), Times.Never)`는 다음으로 바꾼다.

```csharp
            _git.Verify(g => g.GetCommitDetail(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null), Times.Never);
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ObjectHistoryViewModelTests"`
Expected: FAIL — 3인자 생성자, `ChangedFilesNotice`, `ParentCount`, `MergeMark`가 없어 컴파일 오류.

- [ ] **Step 4: Rewrite the view model**

`src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs`에서 생성자·`SelectedEntry`·`SelectedChangedFile`·`UpdateDiffModel`을 아래로 바꾼다. `using DBVC.Vsix.Services;`는 이미 있다.

```csharp
        private readonly IGitManager _gitManager;
        private readonly DiffService _diffService;
        private readonly IBackgroundScheduler _scheduler;

        /// <summary>
        /// 겹친 요청 중 가장 나중 것만 화면에 반영하기 위한 표. 방향키로 이력을 훑으면
        /// 요청이 겹치는데, 없으면 늦게 끝난 앞선 요청이 방금 고른 커밋의 결과를 덮어쓴다.
        /// </summary>
        private int _requestToken;

        public ObjectHistoryViewModel(IGitManager gitManager)
            : this(gitManager, new DiffService(), new InlineBackgroundScheduler())
        {
        }

        public ObjectHistoryViewModel(IGitManager gitManager, DiffService diffService)
            : this(gitManager, diffService, new InlineBackgroundScheduler())
        {
        }

        public ObjectHistoryViewModel(IGitManager gitManager, DiffService diffService, IBackgroundScheduler scheduler)
        {
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            _diffService = diffService ?? throw new ArgumentNullException(nameof(diffService));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }
```

`SelectedEntry` setter를 바꾼다.

```csharp
        private HistoryEntryViewModel? _selectedEntry;
        public HistoryEntryViewModel? SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (ReferenceEquals(_selectedEntry, value)) return;
                _selectedEntry = value;
                OnPropertyChanged();

                ChangedFiles.Clear();
                SetChangedFilesNotice(null);
                _selectedChangedFile = null;
                OnPropertyChanged(nameof(SelectedChangedFile));

                LoadChangedFiles();
                UpdateDiffModel();
            }
        }

        /// <summary>
        /// 전체 이력 모드에서만 변경 파일 목록을 읽는다. 필터 모드는 볼 파일이 이미 정해져 있다.
        /// </summary>
        private void LoadChangedFiles()
        {
            if (_selectedEntry == null || IsSingleObjectMode || ServerName == null || DatabaseName == null)
            {
                return;
            }

            var entry = _selectedEntry;
            var token = ++_requestToken;
            var server = ServerName;
            var database = DatabaseName;
            var sha = ShaOf(entry);

            _scheduler.Run(
                () => _gitManager.GetCommitDetail(server, database, sha, null),
                detail =>
                {
                    // 늦게 끝난 앞선 요청이다. 지금 화면이 보는 커밋과 다르므로 버린다.
                    if (token != _requestToken) return;

                    ChangedFiles.Clear();
                    foreach (var file in detail.ChangedFiles ?? new List<HistoryChangedFile>())
                    {
                        if (file != null) ChangedFiles.Add(HistoryChangedFileViewModel.From(file));
                    }

                    SetChangedFilesNotice(BuildNotice(entry, detail));
                },
                ex => Debug.WriteLine($"ObjectHistoryViewModel.LoadChangedFiles failed: {ex.Message}"));
        }

        private static string? BuildNotice(HistoryEntryViewModel entry, CommitDetail detail)
        {
            var parts = new List<string>();
            if (entry.ParentCount > 1) parts.Add("병합 커밋입니다 — 첫 부모 기준으로 비교합니다.");
            // 상한 상수가 아니라 실제로 담긴 개수를 쓴다. 둘이 어긋나면 안내가 거짓말이 된다.
            if (detail.IsTruncated) parts.Add($"전체 {detail.TotalChangedFileCount}개 중 {detail.ChangedFiles?.Count ?? 0}개만 표시합니다.");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private void SetChangedFilesNotice(string? notice)
        {
            ChangedFilesNotice = notice;
            OnPropertyChanged(nameof(ChangedFilesNotice));
            OnPropertyChanged(nameof(HasChangedFilesNotice));
        }

        /// <summary>파일 목록 위에 띄울 안내. 없으면 <c>null</c>.</summary>
        public string? ChangedFilesNotice { get; private set; }

        public bool HasChangedFilesNotice => !string.IsNullOrEmpty(ChangedFilesNotice);

        /// <summary>축약 SHA는 충돌할 수 있으므로 전체 SHA가 있으면 그것을 쓴다.</summary>
        private static string ShaOf(HistoryEntryViewModel entry)
            => !string.IsNullOrEmpty(entry.Sha) ? entry.Sha : entry.ShortSha;
```

`SelectedChangedFile` setter에서 `UpdateDiffModel()` 호출은 그대로 두고, `UpdateDiffModel`을 바꾼다.

```csharp
        private void UpdateDiffModel()
        {
            var targetPath = IsSingleObjectMode ? RelativePath : _selectedChangedFile?.RelativePath;
            if (_selectedEntry == null || ServerName == null || DatabaseName == null || string.IsNullOrWhiteSpace(targetPath))
            {
                SelectedDiffModel = null;
                return;
            }

            var token = ++_requestToken;
            var server = ServerName;
            var database = DatabaseName;
            var sha = ShaOf(_selectedEntry);
            var path = targetPath!;

            _scheduler.Run(
                () => _gitManager.GetCommitDetail(server, database, sha, path),
                detail =>
                {
                    if (token != _requestToken) return;
                    SelectedDiffModel = _diffService.GetDiffModelFromString(detail.OldText ?? string.Empty, detail.NewText ?? string.Empty);
                },
                ex => Debug.WriteLine($"ObjectHistoryViewModel.UpdateDiffModel failed: {ex.Message}"));
        }
```

`Load`의 초기화 부분에 안내 지우기를 더한다 — `ChangedFiles.Clear();` 아래에 `SetChangedFilesNotice(null);`을 넣는다.

파일 상단 `using`에 `System.Diagnostics`를 더한다.

- [ ] **Step 5: Add `ParentCount`/`MergeMark` to `HistoryEntryViewModel`**

같은 파일 아래쪽 `HistoryEntryViewModel`에서 `ParentSha` 속성 아래에 더하고, `From`에 매핑을 넣는다.

```csharp
        public int ParentCount { get; set; }

        /// <summary>목록에 그대로 찍는 병합 표시. 컨버터를 두지 않으려고 문자열로 낸다.</summary>
        public string MergeMark => ParentCount > 1 ? "병합" : string.Empty;
```

`From` 안 `ParentSha = commit.ParentSha,` 아래에 더한다.

```csharp
                ParentCount = commit.ParentCount,
```

- [ ] **Step 6: Pass the scheduler in**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:98`을 바꾼다.

```csharp
            History = new ObjectHistoryViewModel(_gitManager, new DiffService(), _scheduler);
```

`using DBVC.Vsix.Services;`가 없으면 더한다.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ObjectHistoryViewModelTests"`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels
git commit -m "feat(vsix): 이력 조회를 백그라운드로 옮기고 겹친 요청을 순서대로 반영한다"
```

---

### Task 4: Core — 쓰이지 않게 된 세 메서드를 지운다

Task 3에서 호출부가 모두 옮겨갔다. 같은 사실을 두 경로로 얻는 구조를 남기지 않는다.

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs:123-125`
- Modify: `src/DBVC.Core/GitManager.cs:803-881`
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs:677-798`

**Interfaces:**
- Removes: `GetFileContentAtCommit`, `GetFileContentAtCommitParent`, `GetChangedFilesAtCommit`

- [ ] **Step 1: Confirm there are no remaining callers**

Run: `grep -rn "GetFileContentAtCommit\|GetChangedFilesAtCommit" src/ tests/ --include=*.cs`
Expected: `GitManagerTests.cs`의 옛 테스트와 `Abstractions.cs`/`GitManager.cs`의 선언·정의만 남는다. `src/DBVC.Vsix/` 아래에는 한 건도 없어야 한다. 남아 있으면 Task 3이 덜 끝난 것이므로 되돌아간다.

- [ ] **Step 2: Delete the declarations**

`src/DBVC.Core/Abstractions.cs`에서 세 줄을 지운다.

```csharp
        string? GetFileContentAtCommit(string serverName, string databaseName, string relativeFilePath, string commitSha);
        string? GetFileContentAtCommitParent(string serverName, string databaseName, string relativeFilePath, string commitSha);
        IReadOnlyList<HistoryChangedFile> GetChangedFilesAtCommit(string serverName, string databaseName, string commitSha);
```

- [ ] **Step 3: Delete the implementations**

`src/DBVC.Core/GitManager.cs`에서 `GetFileContentAtCommit`, `GetFileContentAtCommitParent`, `GetChangedFilesAtCommit` 세 메서드를 XML 주석까지 통째로 지운다. `ReadBlobText`는 `GetCommitDetail`과 `GetFileContentBeforeLastCommit`이 계속 쓰므로 남긴다.

- [ ] **Step 4: Delete the obsolete tests**

`tests/DBVC.Core.Tests/GitManagerTests.cs`에서 아래 다섯 테스트와 그 구역 주석(`// ---------- GetFileContentAtCommit & GetFileContentAtCommitParent ----------`, `// ---------- GetChangedFilesAtCommit ----------`)을 지운다. 같은 동작은 Task 1의 `GetCommitDetail` 테스트가 이미 덮는다.

- `GetFileContentAtCommit_ReturnsContentOfCommit_And_GetFileContentAtCommitParent_ReturnsParentContent`
- `GetFileContentAtCommit_ReturnsNull_WhenCommitOrFileDoesNotExist`
- `GetFileContentAtCommitParent_ReturnsNull_WhenCommitDoesNotExistOrUnmapped`
- `GetChangedFilesAtCommit_ReturnsChangedFiles_ForInitialCommit_And_SubsequentCommits`
- `GetChangedFilesAtCommit_ReturnsEmpty_WhenCommitDoesNotExistOrUnmapped`

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Core.Tests -f net10.0 && dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 0 Errors, 전부 PASS

- [ ] **Step 6: Commit**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "refactor(core): GetCommitDetail로 대체된 커밋 조회 메서드를 지운다"
```

---

### Task 5: Vsix — 모드 플래그를 하나로 합치고 진입점을 검증한다

`ViewChangesViewModel.IsSingleObjectMode`를 없애고, `ShowHistoryFor`가 서버까지 검증하며, 호출 순서를 바로잡는다.

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`:115`, `:235`, `:673`, `:688-721`)
- Modify: `src/DBVC.Vsix/Commands/ShowHistoryCommand.cs` (`Execute`)
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Produces: `void ViewChangesViewModel.ShowHistoryFor(string? nodeServerName, string? nodeDatabaseName, string relativePath)`
- Produces: `int ViewChangesViewModel.SelectedTabIndex`
- Removes: `bool ViewChangesViewModel.IsSingleObjectMode`, `ICommand ViewChangesViewModel.ExitSingleObjectModeCommand`

- [ ] **Step 1: Write the failing tests**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`에 더한다. 이 파일에는 `NewViewModel()`(`:91`, 연결 전)과 `NewConnectedViewModel()`(`:102`, `ConnectCommand`까지 태워 `ServerName`/`DatabaseName`이 채워진 상태)이 이미 있으므로 그대로 쓴다.

```csharp
        [Test]
        public void ShowHistoryFor_WarnsAndDoesNotLoad_WhenDbvcIsNotConnected()
        {
            var vm = NewViewModel();   // Connect를 부르지 않은 상태

            vm.ShowHistoryFor(Server, Database, "dbo/Tables/Users.sql");

            Assert.That(vm.WarningMessage, Does.Contain("연결되지 않았습니다"));
            _git.Verify(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void ShowHistoryFor_WarnsWithBothTargets_WhenTheServerDiffers()
        {
            var vm = NewConnectedViewModel();

            vm.ShowHistoryFor("OtherServer", Database, "dbo/Tables/Users.sql");

            Assert.That(vm.WarningMessage, Does.Contain("OtherServer"));
            Assert.That(vm.WarningMessage, Does.Contain(Server));
        }

        [Test]
        public void ShowHistoryFor_WarnsWithBothTargets_WhenTheDatabaseDiffers()
        {
            var vm = NewConnectedViewModel();

            vm.ShowHistoryFor(Server, "OtherDb", "dbo/Tables/Users.sql");

            Assert.That(vm.WarningMessage, Does.Contain("OtherDb"));
            Assert.That(vm.WarningMessage, Does.Contain(Database));
        }

        [Test]
        public void ShowHistoryFor_FiltersHistoryAndSelectsTheHistoryTab_WhenTheTargetMatches()
        {
            var vm = NewConnectedViewModel();

            vm.ShowHistoryFor(Server, Database, "dbo/Tables/Users.sql");

            Assert.That(vm.History.RelativePath, Is.EqualTo("dbo/Tables/Users.sql"));
            Assert.That(vm.History.IsSingleObjectMode, Is.True);
            Assert.That(vm.SelectedTabIndex, Is.EqualTo(1), "이력 탭이 두 번째다");
        }

        [Test]
        public void ShowHistoryFor_KeepsTheFilter_WhenTheObjectIsNotInTheChangeList()
        {
            // 이미 커밋되어 변경 목록에 없는 객체가 이 기능의 주 대상이다.
            // SelectedChange setter를 타면 History가 전체 이력으로 되돌아가 필터가 풀린다.
            var vm = NewConnectedViewModel();
            Assert.That(vm.Changes.Any(c => c.RelativePath == "dbo/Tables/Gone.sql"), Is.False);

            vm.ShowHistoryFor(Server, Database, "dbo/Tables/Gone.sql");

            Assert.That(vm.SelectedChange, Is.Null);
            Assert.That(vm.History.RelativePath, Is.EqualTo("dbo/Tables/Gone.sql"));
        }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ShowHistoryFor"`
Expected: FAIL — 3인자 `ShowHistoryFor`와 `SelectedTabIndex`가 없어 컴파일 오류.

- [ ] **Step 3: Remove the duplicate flag**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`에서 지운다.

- `_isSingleObjectMode` 필드와 `IsSingleObjectMode` 속성 (`:688-698`)
- `ExitSingleObjectModeCommand` 속성과 `ExitSingleObjectMode()` 메서드 (`:700-706`)
- 생성자의 `ExitSingleObjectModeCommand = new RelayCommand(ExitSingleObjectMode);` (`:115`)
- `InvalidateActiveContext` 안의 `IsSingleObjectMode = false;` (`:235`)

- [ ] **Step 4: Add `SelectedTabIndex`**

`History` 속성 근처에 더한다.

```csharp
        /// <summary>비교=0, 이력=1. 개체 탐색기에서 들어오면 이력 탭으로 옮긴다.</summary>
        private const int HistoryTabIndex = 1;

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex == value) return;
                _selectedTabIndex = value;
                OnPropertyChanged();
            }
        }
```

- [ ] **Step 5: Rewrite `ShowHistoryFor`**

`:711-722`의 기존 메서드를 통째로 바꾼다.

```csharp
        /// <summary>
        /// 개체 탐색기에서 고른 객체의 이력을 이력 탭에 띄운다.
        ///
        /// 도구 창은 호출자가 이미 띄운 뒤다 - 안내를 WarningMessage 배너로 내므로
        /// 창을 띄우지 않으면 실패 사유가 사용자에게 보이지 않는다.
        /// </summary>
        public void ShowHistoryFor(string? nodeServerName, string? nodeDatabaseName, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(ServerName) || string.IsNullOrWhiteSpace(DatabaseName))
            {
                WarningMessage = "DBVC가 아직 연결되지 않았습니다. DBVC 창에서 [연결]을 눌러 이 데이터베이스를 대상으로 지정한 뒤 다시 시도하세요.";
                return;
            }

            if (string.IsNullOrWhiteSpace(nodeServerName) || string.IsNullOrWhiteSpace(nodeDatabaseName))
            {
                WarningMessage = "개체 탐색기에서 선택한 노드의 연결 정보를 읽지 못했습니다. 노드를 다시 선택한 뒤 시도하세요.";
                return;
            }

            // 서버까지 본다. DB 이름만 비교하면 서버가 다른 동명 DB의 이력을 이 저장소에서 조용히 읽는다.
            if (!string.Equals(ServerName, nodeServerName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(DatabaseName, nodeDatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                WarningMessage = $"선택한 객체는 {nodeServerName}.{nodeDatabaseName}에 있습니다. DBVC는 지금 {ServerName}.{DatabaseName}에 연결되어 있습니다.";
                return;
            }

            WarningMessage = null;

            // 순서가 중요하다. SelectedChange의 setter가 History를 다시 읽으므로 그것을 태우면
            // 방금 건 필터가 전체 이력으로 덮인다. 뒷단 필드만 맞추고 History는 여기서 한 번만 읽는다.
            _selectedChange = Changes.FirstOrDefault(c =>
                string.Equals(c.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(SelectedChange));

            History.Load(ServerName, DatabaseName, relativePath);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            SelectedTabIndex = HistoryTabIndex;
        }
```

- [ ] **Step 6: Update the command**

`src/DBVC.Vsix/Commands/ShowHistoryCommand.cs`의 `Execute`에서 마지막 두 줄을 바꾼다.

```csharp
            var relativePath = ObjectPathConvention.GetRelativePath(schema, objectType, objectName!);

            // 노드의 연결을 Connect와 같은 경로로 읽는다. URN의 SMO 서버명은 연결 객체의
            // ServerName과 표기가 달라, URN을 파싱해 비교하면 정상 경로까지 막힌다.
            var connection = _source.TryGetCurrent();

            // 안내가 도구 창 배너로 나가므로 실패 경로에서도 창을 먼저 띄운다.
            ShowToolWindow();

            var viewModel = _package.Services.SharedViewChangesViewModel;
            viewModel.ShowHistoryFor(connection?.ServerName, connection?.DatabaseName, relativePath);
```

`databaseName` 지역 변수가 더는 쓰이지 않으면 `out _`로 바꾼다.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS. `IsSingleObjectMode`/`ExitSingleObjectModeCommand`를 참조하던 기존 테스트가 있으면 지우거나 `History.IsSingleObjectMode` 기준으로 고친다.

- [ ] **Step 8: Commit**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/Commands/ShowHistoryCommand.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "fix(vsix): 이력 모드 플래그를 하나로 합치고 진입점에서 서버까지 검증한다"
```

> 이 시점에서 XAML은 아직 `IsSingleObjectMode`를 바인딩한다. WPF 바인딩 실패는 컴파일 오류가 아니라 런타임에 조용히 무시되므로 빌드는 통과한다. Task 6이 그것을 정리한다.

---

### Task 6: UI — 전체화면 블록을 지우고 행을 접는다

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (`:138`, `:207`, `:222-287`, `:291-341`)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` (`:27-28`, `:61-64`, `:70-71`, `:124-127`, `:147-148`, `:215-240`, `:340-342`)

**Interfaces:**
- Consumes: `ViewChangesViewModel.SelectedTabIndex`, `ObjectHistoryViewModel.IsSingleObjectMode`, `ObjectHistoryViewModel.ChangedFilesNotice`, `HistoryEntryViewModel.MergeMark` (Task 3·5)

- [ ] **Step 1: Delete the full-screen block**

`ViewChangesControl.xaml`에서 `<!-- 단일 객체 이력 모드 ... -->` 주석부터 그 `<Grid>`가 닫히는 `</Grid>`까지(`:291-341`) 통째로 지운다.

- [ ] **Step 2: Unhide the normal-mode wrapper**

`:138`의 여는 태그에서 `Visibility` 바인딩을 뺀다. 단일 객체 모드가 사라졌으므로 이 Grid는 늘 보인다.

```xml
            <Grid>
```

- [ ] **Step 3: Bind the tab index**

`:207`의 `TabControl`을 바꾼다.

```xml
                <TabControl Grid.Row="3" SelectedIndex="{Binding SelectedTabIndex, Mode=TwoWay}">
```

- [ ] **Step 4: Replace the history tab**

`<TabItem Header="이력">`부터 그 `</TabItem>`까지(`:222-287`)를 아래로 바꾼다.

```xml
                    <TabItem Header="이력">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="*" />
                            </Grid.RowDefinitions>

                            <!-- 목록만으로는 저장소 전체인지 특정 객체의 이력인지 구분할 수 없다. -->
                            <DockPanel Grid.Row="0" Margin="4,4,4,2" LastChildFill="True">
                                <Button DockPanel.Dock="Right" Content="전체 이력으로" Width="100" Margin="8,0,0,0"
                                        Command="{Binding ShowWholeRepositoryHistoryCommand}"
                                        Visibility="{Binding History.IsSingleObjectMode, Converter={StaticResource BoolToVis}}"
                                        ToolTip="특정 객체로 좁힌 이력을 저장소 전체 이력으로 되돌립니다."/>
                                <TextBlock Text="{Binding History.ScopeLabel}" Foreground="#606060" VerticalAlignment="Center"/>
                            </DockPanel>

                            <Grid Grid.Row="1">
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="1*" />
                                    <RowDefinition Height="5" />
                                    <RowDefinition x:Name="ChangedFilesRow" Height="1*" />
                                    <RowDefinition x:Name="ChangedFilesSplitterRow" Height="5" />
                                    <RowDefinition x:Name="HistoryDiffRow" Height="1*" />
                                </Grid.RowDefinitions>

                                <!-- 상단: 이력 목록 -->
                                <ListView x:Name="HistoryListView" Grid.Row="0" ItemsSource="{Binding History.Entries}"
                                          SelectedItem="{Binding History.SelectedEntry, Mode=TwoWay}"
                                          MouseDoubleClick="HistoryListView_MouseDoubleClick">
                                    <ListView.View>
                                        <GridView>
                                            <GridViewColumn Header="날짜" Width="130" DisplayMemberBinding="{Binding Date}"/>
                                            <GridViewColumn Header="작성자" Width="110" DisplayMemberBinding="{Binding Author}"/>
                                            <GridViewColumn Header="메시지" Width="290" DisplayMemberBinding="{Binding Message}"/>
                                            <GridViewColumn Header="종류" Width="50" DisplayMemberBinding="{Binding MergeMark}"/>
                                            <GridViewColumn Header="SHA" Width="80" DisplayMemberBinding="{Binding ShortSha}"/>
                                        </GridView>
                                    </ListView.View>
                                </ListView>

                                <TextBlock Grid.Row="0" Text="이력이 없습니다." Foreground="#808080"
                                           HorizontalAlignment="Center" VerticalAlignment="Center"
                                           Visibility="{Binding History.IsEmpty, Converter={StaticResource BoolToVis}}"/>

                                <GridSplitter Grid.Row="1" Height="5" HorizontalAlignment="Stretch" Background="Transparent" ShowsPreview="True" Cursor="SizeNS"/>

                                <!-- 중간: 변경된 파일 목록. 필터 모드에서는 행째 접힌다. -->
                                <DockPanel x:Name="ChangedFilesPanel" Grid.Row="2" LastChildFill="True">
                                    <TextBlock DockPanel.Dock="Top" Margin="4,2" Foreground="#6B5A00" TextWrapping="Wrap"
                                               Text="{Binding History.ChangedFilesNotice}"
                                               Visibility="{Binding History.HasChangedFilesNotice, Converter={StaticResource BoolToVis}}"/>
                                    <ListView x:Name="ChangedFilesListView"
                                              ItemsSource="{Binding History.ChangedFiles}"
                                              SelectedItem="{Binding History.SelectedChangedFile, Mode=TwoWay}"
                                              MouseDoubleClick="HistoryListView_MouseDoubleClick">
                                        <ListView.View>
                                            <GridView>
                                                <GridViewColumn Header="상태" Width="80" DisplayMemberBinding="{Binding StateText}"/>
                                                <GridViewColumn Header="객체 유형" Width="110" DisplayMemberBinding="{Binding ObjectTypeText}"/>
                                                <GridViewColumn Header="객체명" Width="260" DisplayMemberBinding="{Binding ObjectName}"/>
                                            </GridView>
                                        </ListView.View>
                                    </ListView>
                                </DockPanel>

                                <GridSplitter x:Name="ChangedFilesSplitter" Grid.Row="3" Height="5" HorizontalAlignment="Stretch" Background="Transparent" ShowsPreview="True" Cursor="SizeNS"/>

                                <!-- 하단: 커밋 Diff 뷰. 볼 것이 없으면 행째 접힌다(코드비하인드). -->
                                <Grid x:Name="HistoryDiffPanel" Grid.Row="4">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="1*"/>
                                        <ColumnDefinition Width="1*"/>
                                    </Grid.ColumnDefinitions>
                                    <avalonEdit:TextEditor x:Name="HistoryOldEditor" Grid.Column="0" IsReadOnly="True" Margin="0,0,2,0" SyntaxHighlighting="TSQL"/>
                                    <avalonEdit:TextEditor x:Name="HistoryNewEditor" Grid.Column="1" IsReadOnly="True" Margin="2,0,0,0" SyntaxHighlighting="TSQL"/>
                                </Grid>
                            </Grid>
                        </Grid>
                    </TabItem>
```

- [ ] **Step 5: Add the "전체 이력으로" command**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`에 더한다. 생성자에서 다른 `RelayCommand`들 옆에 등록한다.

```csharp
        /// <summary>객체로 좁힌 이력을 저장소 전체로 되돌린다.</summary>
        public ICommand ShowWholeRepositoryHistoryCommand { get; }
```

```csharp
            ShowWholeRepositoryHistoryCommand = new RelayCommand(() =>
            {
                _selectedChange = null;
                OnPropertyChanged(nameof(SelectedChange));
                History.Load(ServerName, DatabaseName, null);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            });
```

- [ ] **Step 6: Collapse rows from code-behind**

`RowDefinition`은 시각 트리 밖이라 DataContext를 물려받지 못해 `Height`에 바인딩을 걸 수 없다. 이미 `History.PropertyChanged`를 구독하고 있으므로 그 자리에서 높이를 준다.

`ViewChangesControl.xaml.cs`에서 렌더러 필드 두 개(`_singleHistoryOldRenderer`, `_singleHistoryNewRenderer`)와 그 초기화(`:61-64`), 구독/해제(`:70-71`, `:124-127`, `:147-148`), 핸들러 두 개(`:340`, `:342`)를 지운다. `UpdateHistoryDiffView`에서 `SingleHistory*` 두 줄씩도 지운다.

`OnHistoryPropertyChanged`를 바꾼다.

```csharp
        private void OnHistoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ObjectHistoryViewModel.SelectedDiffModel))
            {
                UpdateHistoryDiffView();
            }
            else if (e.PropertyName == nameof(ObjectHistoryViewModel.IsSingleObjectMode)
                  || e.PropertyName == nameof(ObjectHistoryViewModel.IsDiffVisible))
            {
                UpdateHistoryRowHeights();
            }
        }

        /// <summary>
        /// 필터 모드에서는 변경 파일 목록 행을, 볼 Diff가 없으면 Diff 행을 접는다.
        /// Visibility만으로는 RowDefinition이 자리를 지켜 빈 칸이 화면 1/3을 그대로 차지한다.
        /// RowDefinition은 시각 트리 밖이라 DataContext가 없어 Height에 바인딩을 걸 수 없다 —
        /// 그래서 여기서 준다.
        /// </summary>
        private void UpdateHistoryRowHeights()
        {
            var zero = new System.Windows.GridLength(0);
            var star = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            var splitter = new System.Windows.GridLength(5);

            var single = _viewModel.History.IsSingleObjectMode;
            ChangedFilesRow.Height = single ? zero : star;
            ChangedFilesSplitterRow.Height = single ? zero : splitter;
            ChangedFilesPanel.Visibility = single ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            ChangedFilesSplitter.Visibility = ChangedFilesPanel.Visibility;

            var hasDiff = _viewModel.History.IsDiffVisible;
            HistoryDiffRow.Height = hasDiff ? star : zero;
            HistoryDiffPanel.Visibility = hasDiff ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
```

`UpdateHistoryDiffView`의 두 갈래 끝에서 `UpdateHistoryRowHeights();`를 부른다 — `SelectedDiffModel`이 바뀌면 `IsDiffVisible`도 함께 바뀌지만, 그 알림은 `SelectedDiffModel`보다 뒤에 오므로 여기서 한 번 더 맞춰 두는 편이 순서에 기대지 않는다.

`OnLoaded` 끝의 `UpdateHistoryDiffView();` 아래에 `UpdateHistoryRowHeights();`를 더한다 — 다시 도킹해 붙을 때 현재 모드에 맞춘다.

- [ ] **Step 7: Build**

Run: `dotnet build DBVC.slnx`
Expected: 0 Errors. `SingleHistoryOldEditor` 등 지운 이름을 참조하는 곳이 남아 있으면 컴파일 오류로 잡힌다.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: PASS

- [ ] **Step 9: Commit**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs
git commit -m "refactor(vsix): 전용 전체화면 이력 화면을 지우고 파일 목록 행을 모드에 맞춰 접는다"
```

---

### Task 7: UI — 더블클릭이 원본을 쓰게 하고 임시 파일을 정리한다

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` (`HistoryListView_MouseDoubleClick`, `OnUnloaded` 근처)
- Modify: `src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs`

**Interfaces:**
- Produces: `(string OldText, string NewText)? ObjectHistoryViewModel.GetSelectedFileTexts()`

- [ ] **Step 1: Expose the raw texts on the view model**

`SideBySideDiffModel`에서 Imaginary 줄을 걸러 다시 잇는 지금 방식은 원래 줄 끝과 마지막 개행을 잃는다. 마지막으로 반영된 원본을 그대로 들고 있다가 준다.

`ObjectHistoryViewModel`의 `UpdateDiffModel` 안 `SelectedDiffModel = ...` 바로 위에 더한다.

```csharp
                    _selectedOldText = detail.OldText ?? string.Empty;
                    _selectedNewText = detail.NewText ?? string.Empty;
```

필드와 접근자를 클래스에 더한다. `SelectedDiffModel = null`로 가는 이른 반환 자리에서 둘을 `null`로 되돌린다.

```csharp
        private string? _selectedOldText;
        private string? _selectedNewText;

        /// <summary>
        /// 외부 비교 창에 넘길 원본. Diff 모델에서 되짚어 만들면 줄 끝과 마지막 개행이 달라져
        /// 내장 뷰와 외부 창이 서로 다른 결과를 보인다.
        /// </summary>
        public (string OldText, string NewText)? GetSelectedFileTexts()
            => _selectedOldText == null || _selectedNewText == null ? null : (_selectedOldText, _selectedNewText);
```

- [ ] **Step 2: Rewrite the double-click handler**

`ViewChangesControl.xaml.cs`의 `HistoryListView_MouseDoubleClick`에서 `oldLines`/`newLines`/`oldText`/`newText` 네 줄을 지우고 아래로 바꾼다.

```csharp
            var texts = _viewModel.History.GetSelectedFileTexts();
            if (selected == null || texts == null) return;

            var tempOld = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DBVC_{Guid.NewGuid():N}_old.sql");
            var tempNew = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DBVC_{Guid.NewGuid():N}_new.sql");

            System.IO.File.WriteAllText(tempOld, texts.Value.OldText);
            System.IO.File.WriteAllText(tempNew, texts.Value.NewText);

            // 비교 창이 파일을 붙들고 있으므로 지금 지울 수 없다. 창이 닫힐 때 함께 치운다.
            _tempDiffFiles.Add(tempOld);
            _tempDiffFiles.Add(tempNew);
```

`diffModel` 지역 변수를 쓰던 자리는 `texts`로 바뀌었으므로 그 선언도 지운다.

- [ ] **Step 3: Clean the files up**

필드를 더한다.

```csharp
        /// <summary>외부 비교 창에 넘긴 임시 파일. 창이 붙들고 있어 즉시 지울 수 없다.</summary>
        private readonly System.Collections.Generic.List<string> _tempDiffFiles = new System.Collections.Generic.List<string>();
```

`OnUnloaded`에 더한다.

```csharp
            // Unloaded는 다시 도킹할 때도 뜨므로 목록을 비우고 다시 쌓는다.
            // 비교 창이 아직 열려 있으면 삭제가 실패하는데, 그때는 다음 기회에 지운다.
            foreach (var path in _tempDiffFiles.ToArray())
            {
                try
                {
                    System.IO.File.Delete(path);
                    _tempDiffFiles.Remove(path);
                }
                catch
                {
                    // 비교 창이 아직 쓰고 있다. 다음 Unloaded에서 다시 시도한다.
                }
            }
```

- [ ] **Step 4: Build and test**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 0 Errors, PASS

- [ ] **Step 5: Commit**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs
git commit -m "fix(vsix): 외부 비교 창에 원본을 넘기고 임시 파일을 치운다"
```

---

### Task 8: 문서와 버전

**Files:**
- Modify: `README.md:19`, `README.md:110`
- Modify: `docs/setup-checklist.md`
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest:4`
- Modify: `docs/superpowers/specs/2026-08-31-dbvc-view-history-design.md`
- Modify: `docs/superpowers/plans/2026-09-01-history-diff-view.md`
- Modify: `docs/superpowers/plans/2026-09-02-dbvc-global-history-diff-plan.md`

- [ ] **Step 1: Update README**

`README.md:19`을 바꾼다.

```markdown
- **객체 이력:** 선택한 객체의 커밋 이력을 하단 이력 탭에서 확인하고, 커밋을 고르면 그 시점의 변경 내용을 좌우 Diff로 봅니다. 개체 탐색기에서 객체를 우클릭해 **DBVC: 이력 보기**로 바로 열 수도 있습니다.
```

`README.md:110`을 바꾼다.

```markdown
- **객체 이력:** 목록에서 객체를 선택하고 하단 **이력** 탭을 열면 그 객체의 `.sql` 파일을 변경한 커밋들이 최신순으로 표시됩니다. 객체를 고르지 않으면 저장소 전체 이력이 나오고, 이때는 커밋마다 **변경된 파일 목록**이 함께 뜹니다. 파일을 고르면 아래에 좌우 Diff가 그려지고, 목록을 더블클릭하면 SSMS 내장 비교 창이 새 탭으로 열립니다. 병합 커밋은 **종류** 열에 `병합`으로 표시되며 첫 부모를 기준으로 비교합니다. 객체 이름을 바꾼 커밋은 옛 이름의 `삭제`와 새 이름의 `추가` 두 줄로 나옵니다. 개체 탐색기에서 객체를 우클릭해 **DBVC: 이력 보기**를 누르면 그 객체로 좁힌 이력이 바로 열리며, **전체 이력으로** 버튼으로 되돌립니다.
```

- [ ] **Step 2: Update the setup checklist**

`docs/setup-checklist.md`에서 이력 기능을 다루는 항목을 찾아 위 서술과 맞춘다. 항목이 없으면 확인 절차에 한 줄을 더한다.

```markdown
- [ ] 개체 탐색기에서 커밋된 객체를 우클릭해 **DBVC: 이력 보기**가 뜨고, 눌렀을 때 이력 탭이 그 객체로 좁혀져 열리는지 확인합니다. DBVC가 다른 서버·DB에 연결되어 있으면 그 사실을 알리는 안내가 떠야 합니다.
```

- [ ] **Step 3: Bump the manifest version**

`src/DBVC.Vsix/source.extension.vsixmanifest:4`에서 `Version="0.5.10"`을 `Version="0.5.11"`로 바꾼다.

- [ ] **Step 4: Correct the earlier design docs**

`docs/superpowers/specs/2026-08-31-dbvc-view-history-design.md`의 §2 "객체 식별 로직"에서 VSCT 등록을 지시하는 문장 아래에 더한다.

```markdown
> **실제 구현은 VSCT가 아니다.** SSMS 21의 개체 탐색기 노드 컨텍스트 메뉴에는 확장이 붙을
> 공개 CommandPlacement 지점이 없어, `ShowHistoryCommand`가 `IObjectExplorerService`에서
> WinForms `TreeView`를 리플렉션으로 찾아 `ContextMenuStrip.Opening`을 후킹하고 메뉴 항목을
> 직접 넣는다. 개체 탐색기는 패키지 초기화보다 늦게 뜰 수 있어 2초 폴링 타이머로 재시도한다.
> `DbvcPackage.vsct`의 `ShowHistoryCommandId`(0x0102)는 테스트 상수로만 남아 있다.
```

`docs/superpowers/plans/2026-09-01-history-diff-view.md`의 Task 1 Step 3 코드에서 `ObjectPathConvention.GetRepositoryPath(serverName, databaseName, relativeFilePath)`를 `NormalizePath(relativeFilePath)`로 바꾸고 주석을 더한다.

```markdown
> 저장소 루트가 곧 매핑 경로이므로 서버·DB를 경로에 덧붙이지 않는다.
> `ObjectPathConvention.GetRepositoryPath`라는 메서드는 존재하지 않는다.
```

`docs/superpowers/plans/2026-09-02-dbvc-global-history-diff-plan.md`의 Tech Stack 줄을 바꾼다.

```markdown
**Tech Stack:** C# 8.0, WPF, LibGit2Sharp, NUnit, Moq
```

- [ ] **Step 5: Commit**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest docs/superpowers
git commit -m "docs: 이력 뷰 변경을 문서에 반영하고 선행 설계서의 오기를 고친다"
```

---

## 마무리 — 손으로 확인할 것

CI는 WPF 렌더링, VS 패키지 로딩, `.vsct` 메뉴 등록, SSMS 통합을 검증하지 않는다. 이 작업은 XAML 레이아웃과 개체 탐색기 컨텍스트 메뉴를 정면으로 건드리므로, SSMS 21에서 직접 눌러 보기 전에는 "동작한다"고 말할 수 없다.

- [ ] `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release` 후 `dir src\DBVC.Vsix\bin\Release\net48\*.vsix`로 산출물 존재를 확인한다. 빌드 성공 ≠ `.vsix` 생성이다.
- [ ] SSMS 21에 설치하고 DB에 연결한다.
- [ ] 변경 목록에서 객체를 고르면 이력 탭이 2단(이력 + Diff)으로 접히는지 본다.
- [ ] 아무것도 고르지 않으면 3단(이력 + 파일 목록 + Diff)으로 펴지는지 본다.
- [ ] 개체 탐색기에서 커밋된 객체를 우클릭 → **DBVC: 이력 보기** → 이력 탭이 자동으로 열리고 그 객체로 좁혀지는지 본다.
- [ ] **전체 이력으로**를 눌러 되돌아오는지 본다.
- [ ] 다른 서버·DB의 객체를 우클릭했을 때 두 대상을 모두 알리는 안내가 뜨는지 본다.
- [ ] DBVC를 연결하지 않은 채 우클릭했을 때 연결하라는 안내가 뜨는지 본다.
- [ ] 객체 이름을 바꾸고 커밋한 뒤, 그 커밋에서 `삭제`/`추가` 두 줄이 나오는지 본다.
- [ ] Pull로 병합 커밋을 만든 뒤 **종류** 열에 `병합`이 뜨고 안내가 보이는지 본다.
- [ ] 커밋 목록을 방향키로 빠르게 훑을 때 SSMS가 멈추지 않는지 본다.
