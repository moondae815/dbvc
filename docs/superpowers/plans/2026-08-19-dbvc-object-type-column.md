# Object Type Column Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 'View Changes' 창의 변경 목록(Grid)에 '객체 유형' 컬럼을 추가하여 PROCEDURE, TABLE 등을 SP, Table 등 직관적인 텍스트로 보여준다.

**Architecture:** `ChangeItemViewModel`에 `ObjectType`, `ObjectTypeText` 속성을 추가하고, 매핑 로직(`PROCEDURE` -> `SP` 등)을 구현한다. `ViewChangesViewModel` 갱신 로직에서 이 속성을 할당하며, `ViewChangesControl.xaml`에 해당 속성을 바인딩하는 GridViewColumn을 덧붙인다.

**Tech Stack:** C#, WPF

**Spec:** docs/superpowers/specs/2026-08-19-dbvc-object-type-column-design.md

## Global Constraints

- 추가되는 C# 코드는 기존 프로젝트 컨벤션과 동일한 NUnit 테스트 스타일을 따라야 한다.
- 알 수 없는 객체 유형이 들어올 경우 원본 문자열을 그대로(또는 첫 글자 대문자로) 출력하여 누락 없이 표시해야 한다.

---

### Task 1: View Model 속성 및 매핑 로직 추가

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ChangeItemViewModelTests.cs`

**Interfaces:**
- Produces: `ChangeItemViewModel.ObjectType` (string?), `ChangeItemViewModel.ObjectTypeText` (string)

- [x] **Step 1: Write the failing tests for `ObjectTypeText`**

```csharp
// tests/DBVC.Vsix.Tests/ViewModels/ChangeItemViewModelTests.cs
// 기존 테스트 아래에 추가
        [TestCase("PROCEDURE", "SP")]
        [TestCase("FUNCTION", "UDF")]
        [TestCase("TABLE", "Table")]
        [TestCase("VIEW", "View")]
        [TestCase("TRIGGER", "Trigger")]
        [TestCase("procedure", "SP")]
        public void ObjectTypeText_TranslatesTheCoreObjectType(string type, string expected)
        {
            var item = new ChangeItemViewModel { ObjectType = type };
            Assert.That(item.ObjectTypeText, Is.EqualTo(expected));
        }

        [Test]
        public void ObjectTypeText_PassesThroughAnUnknownType_TitleCased()
        {
            var item = new ChangeItemViewModel { ObjectType = "SYNONYM" };
            Assert.That(item.ObjectTypeText, Is.EqualTo("Synonym"));
        }

        [Test]
        public void ObjectTypeText_IsEmpty_WhenObjectTypeIsNull()
        {
            var item = new ChangeItemViewModel { ObjectType = null };
            Assert.That(item.ObjectTypeText, Is.EqualTo(string.Empty));
        }
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DBVC.Vsix.Tests/DBVC.Vsix.Tests.csproj --filter ChangeItemViewModelTests`
Expected: 컴파일 에러 (`ObjectType`, `ObjectTypeText` 속성 없음) 또는 실패

- [x] **Step 3: Write minimal implementation**

```csharp
// src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs
// 클래스 내부에 추가
        public string? ObjectType { get; set; }

        public string ObjectTypeText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ObjectType))
                    return string.Empty;

                var upperType = ObjectType.Trim().ToUpperInvariant();
                return upperType switch
                {
                    "PROCEDURE" => "SP",
                    "FUNCTION" => "UDF",
                    "TABLE" => "Table",
                    "VIEW" => "View",
                    "TRIGGER" => "Trigger",
                    _ => char.ToUpper(upperType[0]) + upperType.Substring(1).ToLowerInvariant()
                };
            }
        }
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DBVC.Vsix.Tests/DBVC.Vsix.Tests.csproj --filter ChangeItemViewModelTests`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ChangeItemViewModelTests.cs
git commit -m "feat: add ObjectType and ObjectTypeText mapping to ChangeItemViewModel"
```


### Task 2: View Model 바인딩 및 UI 갱신

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`

**Interfaces:**
- Consumes: `ChangeRecord.ObjectType`, `ChangeItemViewModel.ObjectType`, `ChangeItemViewModel.ObjectTypeText`

- [x] **Step 1: 뷰모델 갱신 로직 수정**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` 파일 안의 `ApplyRefreshOutcome` 메서드 내 `Changes.Add(new ChangeItemViewModel ...)` 객체 초기화 부분에 `ObjectType = record.ObjectType`를 추가.

```csharp
                Changes.Add(new ChangeItemViewModel
                {
                    ObjectName = record.QualifiedName,
                    ObjectType = record.ObjectType,
                    State = record.State,
                    RelativePath = record.RelativePath,
                    IsSelected = !cleanupFailed
                });
```

- [x] **Step 2: UI (XAML) 수정**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml` 내 `ListView`의 `GridView` 컬럼에 "객체 유형"을 추가한다. "객체" 컬럼 바로 아래(또는 다음)에 위치시킨다.

```xml
                        <GridViewColumn Header="객체 유형" DisplayMemberBinding="{Binding ObjectTypeText}"/>
```

- [x] **Step 3: 빌드하여 에러가 없는지 확인**

Run: `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj`
Expected: 빌드 성공

- [x] **Step 4: Commit**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml
git commit -m "feat: map ObjectType in ViewModel and show it in UI"
```
