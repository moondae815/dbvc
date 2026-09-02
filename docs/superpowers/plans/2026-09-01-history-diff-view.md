# History Diff View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 이력 보기(History) 시 특정 커밋을 선택하면 해당 시점의 변경된 내용을 Side-by-Side Diff 뷰로 출력하고, 더블클릭 시 SSMS 내장 파일 비교 창을 띄우는 기능 구현

**Architecture:** `IGitManager`를 확장하여 특정 커밋과 부모 커밋의 파일 내용을 가져오는 기능을 추가하고, `ObjectHistoryViewModel`이 이를 사용해 `SideBySideDiffModel`을 생성합니다. UI 탭은 Grid를 상하로 나누어 하단에 내장 `AvalonEdit` 기반 Diff 뷰를 추가합니다.

**Tech Stack:** C# (WPF, MVVM, LibGit2Sharp, AvalonEdit)

---

### Task 1: IGitManager 확장 (커밋 시점 파일 내용 조회)

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Core/GitManager.cs`
- Modify: `tests/DBVC.Core.Tests/GitManagerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/DBVC.Core.Tests/GitManagerTests.cs 끝 부분 혹은 적절한 위치에 추가
[Test]
public void GetFileContentAtCommit_ReturnsContentOfCommit_And_GetFileContentAtCommitParent_ReturnsParentContent()
{
    using var env = new GitTestEnvironment();
    var manager = new GitManager(env.Root, new TestGitManagerContext());
    var dbPath = Path.Combine(env.Root, "Server", "DB");
    Directory.CreateDirectory(dbPath);
    var filePath = Path.Combine(dbPath, "test.sql");

    // Commit 1
    File.WriteAllText(filePath, "V1");
    var sha1 = env.CommitAll("Add V1");

    // Commit 2
    File.WriteAllText(filePath, "V2");
    var sha2 = env.CommitAll("Add V2");

    var v2Content = manager.GetFileContentAtCommit("Server", "DB", "test.sql", sha2);
    var v1Content = manager.GetFileContentAtCommitParent("Server", "DB", "test.sql", sha2);

    Assert.That(v2Content, Is.EqualTo("V1"), "Wait, I wrote V2. The content at sha2 should be V2");
    Assert.That(v1Content, Is.EqualTo("V1"), "Parent content should be V1");
}
```
*참고: 위 테스트 코드는 의도적으로 실패하도록 작성했습니다.*

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj --filter "GetFileContentAtCommit_ReturnsContentOfCommit_And_GetFileContentAtCommitParent_ReturnsParentContent"`
Expected: FAIL (Cannot resolve symbol)

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/DBVC.Core/Abstractions.cs 의 IGitManager 인터페이스에 추가
string? GetFileContentAtCommit(string serverName, string databaseName, string relativeFilePath, string commitSha);
string? GetFileContentAtCommitParent(string serverName, string databaseName, string relativeFilePath, string commitSha);

// src/DBVC.Core/GitManager.cs 에 구현 추가
public string? GetFileContentAtCommit(string serverName, string databaseName, string relativeFilePath, string commitSha)
{
    using var repo = TryOpenRepository();
    if (repo == null) return null;

    var commit = repo.Lookup<LibGit2Sharp.Commit>(commitSha);
    if (commit == null) return null;

    var path = NormalizePath(relativeFilePath);
    return GetContentFromTree(commit.Tree, path);
}

public string? GetFileContentAtCommitParent(string serverName, string databaseName, string relativeFilePath, string commitSha)
{
    using var repo = TryOpenRepository();
    if (repo == null) return null;

    var commit = repo.Lookup<LibGit2Sharp.Commit>(commitSha);
    if (commit == null) return null;

    var parent = commit.Parents.FirstOrDefault();
    if (parent == null) return string.Empty; // 최초 커밋

    var path = NormalizePath(relativeFilePath);
    return GetContentFromTree(parent.Tree, path);
}
```
*그리고 `tests/DBVC.Core.Tests/GitManagerTests.cs`의 테스트 코드에서 `Assert.That(v2Content, Is.EqualTo("V2"));`로 수정.*

> 저장소 루트가 곧 매핑 경로이므로 서버·DB를 경로에 덧붙이지 않는다.
> `ObjectPathConvention.GetRepositoryPath`라는 메서드는 존재하지 않는다.

- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/DBVC.Core.Tests/DBVC.Core.Tests.csproj --filter "GetFileContentAtCommit_ReturnsContentOfCommit_And_GetFileContentAtCommitParent_ReturnsParentContent"`
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): 커밋 및 부모 커밋 시점의 파일 내용을 조회하는 메서드 추가"
```

---

### Task 2: ObjectHistoryViewModel 확장 (Diff 모델 생성 및 더블클릭 커맨드)

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs 에 추가
[Test]
public void SelectedEntry_SetsSelectedDiffModel()
{
    var vm = NewViewModel();
    var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
    vm.ServerName = Server;
    vm.DatabaseName = Database;
    vm.RelativePath = RelativePath; // ViewModel에 속성이 추가되어야 함

    _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, "abcdef1")).Returns("old");
    _git.Setup(g => g.GetFileContentAtCommit(Server, Database, RelativePath, "abcdef1")).Returns("new");

    bool raised = false;
    vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ObjectHistoryViewModel.SelectedDiffModel)) raised = true; };

    vm.SelectedEntry = entry;

    Assert.That(raised, Is.True, "PropertyChanged for SelectedDiffModel should be raised");
    Assert.That(vm.SelectedDiffModel, Is.Not.Null);
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/DBVC.Vsix.Tests/DBVC.Vsix.Tests.csproj --filter "SelectedEntry_SetsSelectedDiffModel"`
Expected: FAIL (Cannot resolve symbols)

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs 수정
// 아래 속성들 추가
public string? ServerName { get; set; }
public string? DatabaseName { get; set; }
public string? RelativePath { get; set; }

private HistoryEntryViewModel? _selectedEntry;
public HistoryEntryViewModel? SelectedEntry
{
    get => _selectedEntry;
    set
    {
        if (ReferenceEquals(_selectedEntry, value)) return;
        _selectedEntry = value;
        OnPropertyChanged();
        UpdateDiffModel();
    }
}

private SideBySideDiffModel? _selectedDiffModel;
public SideBySideDiffModel? SelectedDiffModel
{
    get => _selectedDiffModel;
    private set
    {
        _selectedDiffModel = value;
        OnPropertyChanged();
        OnPropertyChanged(nameof(IsDiffVisible));
    }
}

public bool IsDiffVisible => SelectedDiffModel != null;

// 생성자에 _diffService 추가(또는 내부에서 DiffTextBuilder 사용)
private readonly DiffService _diffService = new DiffService();

private void UpdateDiffModel()
{
    if (_selectedEntry == null || ServerName == null || DatabaseName == null || RelativePath == null)
    {
        SelectedDiffModel = null;
        return;
    }

    var oldContent = _gitManager.GetFileContentAtCommitParent(ServerName, DatabaseName, RelativePath, _selectedEntry.ShortSha);
    var newContent = _gitManager.GetFileContentAtCommit(ServerName, DatabaseName, RelativePath, _selectedEntry.ShortSha);

    SelectedDiffModel = _diffService.GetDiffModelFromString(oldContent ?? "", newContent ?? "");
}

// Load 메서드에서 Context 저장
public void Load(string? serverName, string? databaseName, string? relativePath)
{
    ServerName = serverName;
    DatabaseName = databaseName;
    RelativePath = relativePath;
    // 기존 Load 로직 유지
    Entries.Clear();
    ScopeLabel = string.Empty;
    SelectedEntry = null; // 초기화
    // ... (기존 루프)
```

- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/DBVC.Vsix.Tests/DBVC.Vsix.Tests.csproj --filter "SelectedEntry_SetsSelectedDiffModel"`
Expected: PASS

- [ ] **Step 5: Commit**
```bash
git add src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs
git commit -m "feat(vsix): ObjectHistoryViewModel에 선택된 커밋의 Diff 모델을 제공하는 기능 추가"
```

---

### Task 3: ViewChangesControl.xaml UI 변경 및 코드 비하인드 연결

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs`

- [ ] **Step 1: Modify ViewChangesControl.xaml**

이력(History) 탭 콘텐츠의 Grid를 두 행으로 분할하고, 하단에 AvalonEdit Diff 뷰를 추가합니다. 더블클릭 이벤트도 추가합니다.

```xml
<!-- src/DBVC.Vsix/UI/ViewChangesControl.xaml 의 "이력(History)" TabItem 내부 수정 -->
<Grid Grid.Row="1">
    <Grid.RowDefinitions>
        <RowDefinition Height="1*" />
        <RowDefinition Height="5" />
        <RowDefinition Height="1*" />
    </Grid.RowDefinitions>

    <!-- 상단: 이력 목록 -->
    <ListView x:Name="HistoryListView" Grid.Row="0" ItemsSource="{Binding History.Entries}" SelectedItem="{Binding History.SelectedEntry, Mode=TwoWay}" MouseDoubleClick="HistoryListView_MouseDoubleClick">
        <ListView.View>
            <GridView>
                <GridViewColumn Header="날짜" Width="130" DisplayMemberBinding="{Binding Date}"/>
                <GridViewColumn Header="작성자" Width="110" DisplayMemberBinding="{Binding Author}"/>
                <GridViewColumn Header="메시지" Width="320" DisplayMemberBinding="{Binding Message}"/>
                <GridViewColumn Header="SHA" Width="80" DisplayMemberBinding="{Binding ShortSha}"/>
            </GridView>
        </ListView.View>
    </ListView>

    <TextBlock Grid.Row="0" Text="이력이 없습니다." Foreground="#808080"
               HorizontalAlignment="Center" VerticalAlignment="Center"
               Visibility="{Binding History.IsEmpty, Converter={StaticResource BoolToVis}}"/>

    <GridSplitter Grid.Row="1" Height="5" HorizontalAlignment="Stretch" Background="Transparent" ShowsPreview="True" Cursor="SizeNS"/>

    <!-- 하단: 커밋 Diff 뷰 -->
    <Grid Grid.Row="2" Visibility="{Binding History.IsDiffVisible, Converter={StaticResource BoolToVis}}">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="1*"/>
            <ColumnDefinition Width="1*"/>
        </Grid.ColumnDefinitions>
        <avalonEdit:TextEditor x:Name="HistoryOldEditor" Grid.Column="0" IsReadOnly="True" Margin="0,0,2,0" Background="{DynamicResource {x:Static vsshell:VsBrushes.WindowKey}}" Foreground="{DynamicResource {x:Static vsshell:VsBrushes.WindowTextKey}}"/>
        <avalonEdit:TextEditor x:Name="HistoryNewEditor" Grid.Column="1" IsReadOnly="True" Margin="2,0,0,0" Background="{DynamicResource {x:Static vsshell:VsBrushes.WindowKey}}" Foreground="{DynamicResource {x:Static vsshell:VsBrushes.WindowTextKey}}"/>
    </Grid>
</Grid>

<!-- 단일 객체 모드 쪽 화면도 동일한 레이아웃으로 변경 (ViewChangesControl.xaml 260~290 줄 근처) -->
```
*(단일 객체 모드(Single Object Mode) 쪽의 History ListView 영역도 위와 동일하게 Grid Row 분할 구조로 적용해 줍니다.)*

- [ ] **Step 2: Modify ViewChangesControl.xaml.cs**

코드 비하인드에서 렌더러 추가, 이벤트 바인딩 및 더블클릭 시 Visual Studio Diff 창 호출 로직을 구현합니다.

```csharp
// src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs 에 렌더러 변수 추가
private readonly DiffLineBackgroundRenderer _historyOldRenderer;
private readonly DiffLineBackgroundRenderer _historyNewRenderer;

// 생성자에 초기화 추가
_historyOldRenderer = new DiffLineBackgroundRenderer(HistoryOldEditor.TextArea.TextView);
_historyNewRenderer = new DiffLineBackgroundRenderer(HistoryNewEditor.TextArea.TextView);
HistoryOldEditor.TextArea.TextView.BackgroundRenderers.Add(_historyOldRenderer);
HistoryNewEditor.TextArea.TextView.BackgroundRenderers.Add(_historyNewRenderer);
HistoryOldEditor.TextArea.TextView.ScrollOffsetChanged += (s, e) => SyncScroll(HistoryOldEditor, HistoryNewEditor);
HistoryNewEditor.TextArea.TextView.ScrollOffsetChanged += (s, e) => SyncScroll(HistoryNewEditor, HistoryOldEditor);

// OnLoaded / OnUnloaded 에 History 속성 변경 이벤트 구독 연결
private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
{
    // 기존...
    _viewModel.History.PropertyChanged -= OnHistoryPropertyChanged;
    _viewModel.History.PropertyChanged += OnHistoryPropertyChanged;
}

private void OnHistoryPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(ObjectHistoryViewModel.SelectedDiffModel))
    {
        var model = _viewModel.History.SelectedDiffModel;
        if (model != null)
        {
            ApplyDiffPanes(model, HistoryOldEditor, _historyOldRenderer, HistoryNewEditor, _historyNewRenderer);
        }
    }
}

// 더블클릭 이벤트
private void HistoryListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
{
    var selected = _viewModel.History.SelectedEntry;
    var diffModel = _viewModel.History.SelectedDiffModel;
    if (selected == null || diffModel == null) return;

    var tempOld = System.IO.Path.GetTempFileName();
    var tempNew = System.IO.Path.GetTempFileName();
    
    System.IO.File.WriteAllText(tempOld, diffModel.OldText.OriginalText ?? "");
    System.IO.File.WriteAllText(tempNew, diffModel.NewText.OriginalText ?? "");

    var diffService = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsDifferenceService)) as Microsoft.VisualStudio.Shell.Interop.IVsDifferenceService;
    
    if (diffService != null)
    {
        diffService.OpenComparisonWindow2(
            tempOld, tempNew, 
            $"{_viewModel.History.RelativePath} ({selected.ShortSha}^)", 
            $"{_viewModel.History.RelativePath} ({selected.ShortSha})", 
            $"DBVC Commit: {selected.ShortSha}", 
            $"DBVC Commit: {selected.ShortSha}", 
            "DBVC", "", 0);
    }
}
```

- [ ] **Step 3: Run build to verify it compiles**
Run: `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj`
Expected: 0 Errors

- [ ] **Step 4: Commit**
```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs
git commit -m "feat(vsix): 이력 보기 시 Side-by-Side Diff 뷰 추가 및 더블클릭 시 SSMS 내장 Diff 창 연동"
```
