# 개체 탐색기 객체별 변경 이력 조회 (DBVC View History) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SSMS 개체 탐색기 우클릭 메뉴를 통해 특정 데이터베이스 객체의 DBVC 커밋 이력을 손쉽게 조회할 수 있게 한다.

**Architecture:** 개체 탐색기의 컨텍스트 메뉴 명령을 통해 `ViewChangesToolWindow`를 활성화하고, `ViewChangesViewModel`을 '단일 객체 모드'로 전환하여 `ObjectHistoryViewModel`을 전체 영역에 띄운다. `SsmsUrn` 클래스를 확장하여 개체 탐색기 URN 문자열에서 객체 정보를 파싱해낸다.

**Tech Stack:** C# 8.0, WPF (MVVM), Visual Studio Extensibility (VSSDK, VSCT)

**Spec:** `docs/superpowers/specs/2026-08-31-dbvc-view-history-design.md`

## Global Constraints

- 저장소 경로는 `ObjectPathConvention` 한 곳에서만 정한다: `[Schema]/[ObjectType]/[Name].sql`, 구분자는 항상 `/`.
- 모든 UI 텍스트(메뉴, 알림, 에러)는 한국어로 작성한다.
- SSMS 셸 어셈블리는 컴파일 타임에 직접 참조하지 않으며(`SsmsUrn` 또는 리플렉션 활용), GAC/패키지 버전 고정 규칙을 따른다.
- 테스트 프로젝트는 DB/Git 없이 실행될 수 있어야 하며 인터페이스 모의(Mocking)를 적극 활용한다.

---

### Task 1: SsmsUrn을 확장하여 Object Identity 추출하기

**Files:**
- Modify: `src/DBVC.Vsix/Services/SsmsUrn.cs`
- Create: `tests/DBVC.Vsix.Tests/Services/SsmsUrnTests.cs` (if doesn't exist, otherwise Modify)

**Interfaces:**
- Produces: `public static bool TryParseObjectIdentity(string? urn, out string? databaseName, out string? schema, out string? objectType, out string? objectName)`

- [ ] **Step 1: Write the failing tests for URN parsing**
```csharp
// tests/DBVC.Vsix.Tests/Services/SsmsUrnTests.cs
using System;
using NUnit.Framework;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class SsmsUrnTests
    {
        [Test]
        public void TryParseObjectIdentity_ValidTableUrn_ReturnsTrueAndExtractsParts()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']/Table[@Name='Person' and @Schema='dbo']";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.IsTrue(result);
            Assert.AreEqual("SalesDB", db);
            Assert.AreEqual("dbo", schema);
            Assert.AreEqual("Table", type);
            Assert.AreEqual("Person", name);
        }

        [Test]
        public void TryParseObjectIdentity_InvalidUrn_ReturnsFalse()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']/Tables";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.IsFalse(result);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~SsmsUrnTests"`
Expected: FAIL due to missing method.

- [ ] **Step 3: Implement `TryParseObjectIdentity` in `SsmsUrn`**
```csharp
// In src/DBVC.Vsix/Services/SsmsUrn.cs
public static bool TryParseObjectIdentity(string? urn, out string? databaseName, out string? schema, out string? objectType, out string? objectName)
{
    databaseName = TryGetDatabaseName(urn);
    schema = null;
    objectType = null;
    objectName = null;

    if (string.IsNullOrEmpty(urn) || databaseName == null) return false;

    // Example naive parse - extract the last segment
    // /Database[@Name='...']/ObjectType[@Name='...' and @Schema='...']
    var lastSlash = urn.LastIndexOf('/');
    if (lastSlash < 0) return false;

    var lastSegment = urn.Substring(lastSlash + 1);
    var bracketIndex = lastSegment.IndexOf('[');
    if (bracketIndex < 0) return false;

    objectType = lastSegment.Substring(0, bracketIndex);

    // Extract Name
    var nameMarker = "@Name='";
    var nameStart = lastSegment.IndexOf(nameMarker, StringComparison.Ordinal);
    if (nameStart > 0)
    {
        nameStart += nameMarker.Length;
        var nameEnd = lastSegment.IndexOf('\'', nameStart);
        if (nameEnd > nameStart)
        {
            objectName = lastSegment.Substring(nameStart, nameEnd - nameStart);
        }
    }

    // Extract Schema
    var schemaMarker = "@Schema='";
    var schemaStart = lastSegment.IndexOf(schemaMarker, StringComparison.Ordinal);
    if (schemaStart > 0)
    {
        schemaStart += schemaMarker.Length;
        var schemaEnd = lastSegment.IndexOf('\'', schemaStart);
        if (schemaEnd > schemaStart)
        {
            schema = lastSegment.Substring(schemaStart, schemaEnd - schemaStart);
        }
    }
    
    return !string.IsNullOrEmpty(objectName);
}
```

- [ ] **Step 4: Verify test passes**
Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~SsmsUrnTests"`
Expected: PASS

- [ ] **Step 5: Commit**
Run: `git commit -am "feat(core): SsmsUrn에 객체 식별자 추출 로직 추가"`

---

### Task 2: ViewModel 상태 관리 (단일 객체 모드)

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`

**Interfaces:**
- Produces: `public bool IsSingleObjectMode { get; }`, `public ICommand ExitSingleObjectModeCommand { get; }`, `public void ShowHistoryFor(string databaseName, string relativePath)`

- [ ] **Step 1: `IsSingleObjectMode` 속성 및 `ExitSingleObjectModeCommand` 추가**
`ViewChangesViewModel.cs`에 속성 및 커맨드를 추가하여 단일 객체 모드를 토글할 수 있게 한다.
```csharp
private bool _isSingleObjectMode;
public bool IsSingleObjectMode
{
    get => _isSingleObjectMode;
    private set
    {
        if (_isSingleObjectMode == value) return;
        _isSingleObjectMode = value;
        OnPropertyChanged();
    }
}

public ICommand ExitSingleObjectModeCommand { get; }

// 생성자에 추가:
// ExitSingleObjectModeCommand = new RelayCommand(() => IsSingleObjectMode = false);
```

- [ ] **Step 2: `ShowHistoryFor` 메서드 추가**
```csharp
public void ShowHistoryFor(string databaseName, string relativePath)
{
    if (DatabaseName != databaseName)
    {
        // 대상 DB가 다르면 현재 연결이 아니므로 전환이 필요하지만 
        // 일단 현재는 경고 처리 또는 무시. (실제 구현에서는 연결을 자동 전환하거나 에러 표시)
        WarningMessage = $"선택한 객체는 현재 활성화된 DB({DatabaseName})에 속하지 않습니다.";
        return;
    }

    IsSingleObjectMode = true;
    History.Load(ServerName, DatabaseName, relativePath);
}
```
*(참고: `InvalidateActiveContext` 호출 시 `IsSingleObjectMode = false;`로 초기화되도록 추가)*

- [ ] **Step 3: 테스트 및 Commit**
(단위 테스트 작성 생략: 기존 ViewModel 인프라를 활용하므로 빌드 성공 확인)
`dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj`
`git commit -am "feat(vsix): 단일 객체 이력 모드 ViewModel 구현"`

---

### Task 3: UI 변경 사항 (ViewChangesControl)

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`

**Interfaces:**
- Consumes: `IsSingleObjectMode`, `ExitSingleObjectModeCommand` from Task 2.

- [ ] **Step 1: XAML 레이아웃 수정**
기존 `ViewChangesControl.xaml`에서 상단의 DataGrid와 하단의 History 영역 사이에 트리거를 추가한다. `IsSingleObjectMode`가 `True`일 때 상단 영역은 `Collapsed`, 하단 영역 상단에 "돌아가기" 버튼이 보이도록 한다.

```xml
<!-- 리소스에 BoolToVis 반대 컨버터 등이 있는지 확인, 없다면 생성하거나 DataTrigger 사용 -->
<Style x:Key="ChangeListStyle" TargetType="Grid">
    <Style.Triggers>
        <DataTrigger Binding="{Binding IsSingleObjectMode}" Value="True">
            <Setter Property="Visibility" Value="Collapsed"/>
        </DataTrigger>
    </Style.Triggers>
</Style>
```
하단 History 영역 상단에 버튼 추가:
```xml
<StackPanel Visibility="{Binding IsSingleObjectMode, Converter={StaticResource BoolToVis}}">
    <Button Command="{Binding ExitSingleObjectModeCommand}" Content="🔙 변경 사항 목록으로 돌아가기" Margin="0,0,0,10" />
</StackPanel>
```

- [ ] **Step 2: 컴파일 및 Commit**
`dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj`
`git commit -am "feat(vsix): 이력 모드 진입/해제 UI 구현"`

---

### Task 4: VSCT 확장 및 커맨드 바인딩

**Files:**
- Modify: `src/DBVC.Vsix/DbvcPackage.vsct`
- Modify: `src/DBVC.Vsix/DbvcPackage.cs`
- Create: `src/DBVC.Vsix/Commands/ShowHistoryCommand.cs`

**Interfaces:**
- Consumes: `SsmsUrn.TryParseObjectIdentity` (Task 1)

- [ ] **Step 1: `DbvcPackage.vsct`에 메뉴 추가**
`<Symbols>`에 새 CommandID (`ShowHistoryCommandId = 0x0102`) 추가.
`ObjectExplorer` 컨텍스트 메뉴로 추정되는 곳에 메뉴 버튼 배치. SSMS 개체 탐색기 트리용 메뉴 그룹으로 등록 (`IDM_VS_CTXT_ITEMNODE` 등에 시도하거나 SSMS 고유 GUID 활용).
```xml
<Button guid="guidDbvcPackageCmdSet" id="ShowHistoryCommandId" priority="0x0100" type="Button">
    <!-- Parent는 Object Explorer 컨텍스트 그룹 -->
</Button>
```

- [ ] **Step 2: `ShowHistoryCommand` 구현**
```csharp
// src/DBVC.Vsix/Commands/ShowHistoryCommand.cs
using System;
using DBVC.Core;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Commands
{
    public class ShowHistoryCommand
    {
        public static void Initialize(DbvcPackage package, ObjectExplorerConnectionSource source)
        {
            // 메뉴 핸들러 등록.
            // 실행 시 source에서 INodeContext를 읽고 TryParseObjectIdentity 수행 후
            // ObjectPathConvention.GetRelativePath(schema, type, name) 생성
            // package.ShowToolWindow() 를 호출하여 창 띄우고 ViewModel 가져옴
            // viewModel.ShowHistoryFor(dbName, relativePath) 호출
        }
    }
}
```

- [ ] **Step 3: `DbvcPackage.cs`에 등록**
`InitializeAsync` 메서드 안에서 `ShowHistoryCommand.Initialize(this, connectionSource);` 호출.

- [ ] **Step 4: 빌드 및 Commit**
`dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj`
`git commit -am "feat(vsix): 개체 탐색기 컨텍스트 메뉴에 이력 보기 커맨드 등록"`

---
