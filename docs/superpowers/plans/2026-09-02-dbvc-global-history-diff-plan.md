# Global History Changed Files Diff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 전체 이력 보기 모드에서 특정 커밋을 선택했을 때 해당 커밋에 변경된 파일 목록을 보여주고, 파일을 선택하면 Diff를 표시합니다.

**Architecture:** IGitManager에 파일 변경점 조회 메서드를 추가하고 ObjectHistoryViewModel에서 이를 바인딩할 수 있도록 ChangedFiles 속성을 추가합니다. ViewChangesControl.xaml은 이 속성을 바인딩하여 3단 수직 분할 뷰로 렌더링합니다.

**Tech Stack:** C# 10, WPF, LibGit2Sharp, xUnit, Moq

**Spec:** docs/superpowers/specs/2026-09-02-dbvc-global-history-diff-design.md

## Global Constraints

- 모든 UI 텍스트(메뉴, 알림, 에러)는 한국어로 작성한다.
- 테스트 프로젝트는 DB/Git 없이 실행될 수 있어야 하며 인터페이스 모의(Mocking)를 적극 활용한다.

---

### Task 1: GitManager 기능 추가 (GetChangedFilesAtCommit)

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Core/GitManager.cs`
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs`
- Create: `src/DBVC.Core/HistoryChangedFile.cs`

**Interfaces:**
- Produces: `IReadOnlyList<HistoryChangedFile> GetChangedFilesAtCommit(string serverName, string databaseName, string commitSha)`

- [ ] **Step 1: Write the failing test**
In `GitManagerTests.cs`, add a test to verify `GetChangedFilesAtCommit` returns correct added/modified/deleted files between two commits. Since GitManager is hard to test without a real repo, follow existing patterns (maybe unit tests use a temp repo or integration tests).

- [ ] **Step 2: Add Model and Interface**
In `Abstractions.cs` (or `HistoryChangedFile.cs`), add `public enum HistoryChangedFileState { Added, Modified, Deleted }` and `public class HistoryChangedFile { public HistoryChangedFileState State { get; set; } public string RelativePath { get; set; } }`. Add the method to `IGitManager`.

- [ ] **Step 3: Write minimal implementation**
In `GitManager.cs`, implement `GetChangedFilesAtCommit`.
```csharp
public IReadOnlyList<HistoryChangedFile> GetChangedFilesAtCommit(string serverName, string databaseName, string commitSha)
{
    var repoPath = ResolveRepoPath(serverName, databaseName);
    if (repoPath == null) return new List<HistoryChangedFile>();
    
    using var repo = new Repository(repoPath);
    var commit = repo.Lookup<Commit>(commitSha);
    if (commit == null) return new List<HistoryChangedFile>();
    
    var parentTree = commit.Parents.FirstOrDefault()?.Tree; // null for initial commit
    var changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);
    
    return changes.Select(c => new HistoryChangedFile {
        State = c.Status == ChangeKind.Added ? HistoryChangedFileState.Added :
                c.Status == ChangeKind.Deleted ? HistoryChangedFileState.Deleted :
                HistoryChangedFileState.Modified,
        RelativePath = c.Path
    }).ToList();
}
```

- [ ] **Step 4: Run tests to verify it passes**
Run: `dotnet test tests/DBVC.Core.Tests`
Expected: PASS

- [ ] **Step 5: Commit**
`git add src/DBVC.Core tests/DBVC.Core.Tests`
`git commit -m "feat(core): add GetChangedFilesAtCommit to IGitManager"`

---

### Task 2: ViewModel 상태 추가 (ObjectHistoryViewModel)

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs`
- Modify: `tests/DBVC.Vsix.Tests/ObjectHistoryViewModelTests.cs`
- Create: `src/DBVC.Vsix/ViewModels/HistoryChangedFileViewModel.cs`

**Interfaces:**
- Consumes: `IGitManager.GetChangedFilesAtCommit`
- Produces: `ObservableCollection<HistoryChangedFileViewModel> ChangedFiles`, `HistoryChangedFileViewModel SelectedChangedFile`

- [ ] **Step 1: Write the failing test**
In `ObjectHistoryViewModelTests.cs`, write a test that verifies when `SelectedEntry` changes (and `IsSingleObjectMode` is false), `ChangedFiles` is populated by calling `GetChangedFilesAtCommit`. Also verify `SelectedDiffModel` updates when `SelectedChangedFile` is set.

- [ ] **Step 2: Add HistoryChangedFileViewModel**
Create `HistoryChangedFileViewModel.cs` converting `HistoryChangedFile` into bindable properties (`StateText`, `ObjectType`, `ObjectName`).

- [ ] **Step 3: Write minimal implementation**
In `ObjectHistoryViewModel.cs`:
```csharp
public ObservableCollection<HistoryChangedFileViewModel> ChangedFiles { get; } = new ObservableCollection<HistoryChangedFileViewModel>();

private HistoryChangedFileViewModel _selectedChangedFile;
public HistoryChangedFileViewModel SelectedChangedFile
{
    get => _selectedChangedFile;
    set
    {
        if (ReferenceEquals(_selectedChangedFile, value)) return;
        _selectedChangedFile = value;
        OnPropertyChanged();
        UpdateDiffModel();
    }
}
```
Modify `SelectedEntry` setter to fetch changed files if `RelativePath` is null.
Modify `UpdateDiffModel` to use `SelectedChangedFile.RelativePath` instead of `RelativePath` when `RelativePath` is null (global mode).

- [ ] **Step 4: Run tests to verify it passes**
Run: `dotnet test tests/DBVC.Vsix.Tests`
Expected: PASS

- [ ] **Step 5: Commit**
`git add src/DBVC.Vsix/ViewModels tests/DBVC.Vsix.Tests`
`git commit -m "feat(vsix): add ChangedFiles tracking to ObjectHistoryViewModel"`

---

### Task 3: UI 레이아웃 업데이트 (ViewChangesControl.xaml)

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`

**Interfaces:**
- Consumes: `ChangedFiles`, `SelectedChangedFile`

- [ ] **Step 1: Update XAML Grid Layout**
In `ViewChangesControl.xaml` under `<TabItem Header="이력">` for the non-single-object mode Grid (the inner Grid inside `Grid.Row="1"`), change RowDefinitions from 3 rows to 5 rows to accommodate the new list and splitter.

- [ ] **Step 2: Add ChangedFilesListView**
Add a `ListView` bound to `ChangedFiles` with `SelectedItem="{Binding SelectedChangedFile, Mode=TwoWay}"` at Grid.Row="2". Add columns for 상태, 객체 유형, 객체명.

- [ ] **Step 3: Adjust Diff View**
Move the `GridSplitter` to Grid.Row="3" and the Diff View Grid to Grid.Row="4".

- [ ] **Step 4: Verify build**
Run: `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj`
Expected: PASS with 0 Errors.

- [ ] **Step 5: Commit**
`git add src/DBVC.Vsix/UI/ViewChangesControl.xaml`
`git commit -m "feat(vsix): display changed files list in global history view"`
