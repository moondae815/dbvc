# DBVC UI 문구 한국어 통일 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DBVC 화면에 영어로 남아 있는 버튼·컬럼·탭·문장·메뉴·확장 정보를 한국어로 통일한다.

**Architecture:** 문자열 교체가 대부분이다. 구조가 바뀌는 곳은 둘뿐 — 상태값을 표시 계층에서 옮기는 `ChangeItemViewModel.StateText` 속성 하나와, 대화상자 제목과 기본 파일명에 함께 쓰이던 지역 변수 하나를 둘로 가르는 것. **Core는 전혀 바뀌지 않는다.**

**Tech Stack:** C# / .NET Framework 4.8 (`LangVersion latest`, `Nullable enable`), WPF(MVVM), VSIX(`.vsct`), NUnit 4, Moq.

**설계 문서:** [docs/superpowers/specs/2026-08-18-dbvc-korean-ui-design.md](../specs/2026-08-18-dbvc-korean-ui-design.md)

## Global Constraints

- **`Commit`·`Pull`·`Push` 버튼은 영어로 둔다.** Git 클라이언트·문서와 용어가 어긋나면 대응이 끊긴다. 이 세 글자를 한국어로 바꾸면 계획 위반이다.
- **`SHA` 컬럼 헤더는 영어로 둔다.** 번역어가 없는 식별자 형식 이름이고 사용자가 값을 그대로 복사해 쓴다.
- **생성된 `.sql` 파일의 헤더는 영어로 둔다.** `DBVC Deployment Script`·`Generated:`·`Objects:`·`Excluded:` 는 `ScriptGenerator.cs` 소관이며 **이 계획은 그 파일을 건드리지 않는다.**
- **`source.extension.vsixmanifest` 의 `Language="en-US"` 와 `<Tags>` 는 바꾸지 않는다.** 전자는 설치 대상 판정에 관여할 수 있고 바꿔야 할 측정된 이유가 없다. 후자는 검색어이지 표시 문구가 아니다.
- **Core(`src/DBVC.Core/`)를 수정하지 않는다.** 상태값 `Added`·`Modified`·`Deleted` 는 데이터다 — `WorkingTreeCleaner` 가 삭제 판정에 쓰고 Core 테스트 여럿이 문자열로 검증한다.
- **코드 식별자를 이름 바꾸지 않는다.** `ViewChangesToolWindow`·`ViewChangesViewModel`·`ViewChangesControl`·`ViewChangesCommand` 는 사용자에게 보이지 않는다.
- **패키지 버전을 올리지 않는다.** `LibGit2Sharp 0.32.0`, `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0` 은 SSMS 21이 프로세스에 먼저 올리는 어셈블리에 맞춰 고정돼 있다.
- **주석은 "왜"만 적는다.** 기존 주석 밀도와 문체(한국어 평서문)를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제.** 기존 이력 형태: `feat(vsix): View Changes 창에 Push 버튼을 더한다`.
- **테스트 실행 명령**
  ```bash
  dotnet test tests/DBVC.Vsix.Tests
  dotnet test tests/DBVC.Core.Tests
  dotnet build DBVC.slnx
  ```
  단일 테스트: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~테스트이름"`
- **시작 상태:** Vsix 151 passing, Core 261 passing, 0 failing, net48·net10.0 양쪽. 빌드 0 errors.

---

## File Structure

**수정만 한다. 새 파일은 없다.**

| 파일 | 변경 | 태스크 |
| --- | --- | --- |
| `src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs` | `StateText` 표시 전용 속성 | 1 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | `State` 컬럼 바인딩 | 1 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 버튼·컬럼·탭 라벨, 문장 하나, 버튼 폭 | 2 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 경고 문구 상수 | 2 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | 스크립트 제목·파일명 분리 | 3 |
| `src/DBVC.Vsix/Services/IFileSaveDialog.cs` | 대화상자 필터 | 3 |
| `src/DBVC.Vsix/DbvcPackage.vsct` | 컨텍스트 메뉴 문구 | 4 |
| `src/DBVC.Vsix/source.extension.vsixmanifest` | 확장 이름·설명 | 4 |
| `src/DBVC.Vsix/Commands/CompareWithRepositoryCommand.cs` | XML 주석의 메뉴 이름 | 4 |
| `tests/DBVC.Vsix.Tests/ViewModels/ChangeItemViewModelTests.cs` | `StateText` 테스트 | 1 |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | 주석 한 줄 | 2 |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | 알림 제목 검증 갱신 | 3 |
| `README.md`, `docs/setup-checklist.md` | 라벨 참조, "View Changes" 명칭 | 5 |

`ViewChangesControl.xaml` 과 `ViewChangesViewModel.cs` 를 여러 태스크가 건드리지만 순차 실행이므로 충돌하지 않는다. 태스크 1은 **상태값 하나**를, 태스크 2는 **나머지 라벨 전부**를 맡는다 — 태스크 1이 자체 테스트를 갖는 유일한 코드 변경이라 따로 뗀다.

---

## Task 1: 상태값을 표시 계층에서 한국어로 옮긴다

Core의 `State` 는 데이터로 그대로 두고, 화면에 뿌릴 때만 옮긴다.

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (93행 `State` 컬럼)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ChangeItemViewModelTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `string ChangeItemViewModel.StateText { get; }` — `State` 가 `"Added"`/`"Modified"`/`"Deleted"` 면 각각 `"추가"`/`"수정"`/`"삭제"`, 그 외 값이면 **그 값 그대로**, `null` 이면 빈 문자열.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`ChangeItemViewModelTests.cs` 의 마지막 테스트(`IsSelected_RaisesPropertyChanged`) 아래에 넣는다.

```csharp
        [TestCase("Added", "추가")]
        [TestCase("Modified", "수정")]
        [TestCase("Deleted", "삭제")]
        public void StateText_TranslatesTheCoreState(string state, string expected)
        {
            var item = new ChangeItemViewModel { State = state };

            Assert.That(item.StateText, Is.EqualTo(expected));
        }

        [Test]
        public void StateText_PassesThroughAnUnknownState()
        {
            // Core가 새 상태값을 내놓게 되면 조용히 빈칸이 되는 대신 원문이 보여야 한다.
            // 번역표에 없는 값이 생겼다는 사실 자체가 화면에 드러나야 알아챌 수 있다.
            var item = new ChangeItemViewModel { State = "Renamed" };

            Assert.That(item.StateText, Is.EqualTo("Renamed"));
        }

        [Test]
        public void StateText_IsEmpty_WhenStateIsNull()
        {
            var item = new ChangeItemViewModel { State = null };

            Assert.That(item.StateText, Is.EqualTo(string.Empty));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~StateText"`
Expected: 컴파일 실패 — `StateText` 가 없다.

- [ ] **Step 3: `StateText` 를 더한다**

`ChangeItemViewModel.cs` 의 `State` 속성 **바로 아래**에 넣는다. 기존 `State` 줄의 꼬리 주석(`// "Modified", "Added", "Deleted"`)은 그대로 둔다 — Core가 주는 값이 무엇인지 말하고 있고 여전히 참이다.

```csharp
        /// <summary>
        /// 화면에 뿌리는 상태. Core의 <see cref="State"/>는 데이터로 남긴다 —
        /// WorkingTreeCleaner가 삭제 판정에 쓰고 Core 테스트가 문자열로 검증한다.
        ///
        /// 번역표에 없는 값은 원문을 그대로 통과시킨다. 조용히 빈칸이 되면
        /// Core가 새 상태를 내놓기 시작해도 알아챌 방법이 없다.
        /// </summary>
        public string StateText => State switch
        {
            "Added" => "추가",
            "Modified" => "수정",
            "Deleted" => "삭제",
            _ => State ?? string.Empty
        };
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~StateText"`
Expected: PASS (5개 — `TestCase` 3 + 개별 2)

- [ ] **Step 5: XAML 바인딩을 옮긴다**

`ViewChangesControl.xaml` 93행. **헤더는 이 태스크에서 바꾸지 않는다**(태스크 2 소관). 바인딩만 옮긴다.

```xml
                        <GridViewColumn Header="State" DisplayMemberBinding="{Binding StateText}"/>
```

- [ ] **Step 6: 전체 테스트가 그대로 통과하는지 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests && dotnet test tests/DBVC.Core.Tests`
Expected: Vsix 156 passing, Core 261 passing, 0 failing

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ChangeItemViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/ViewModels/ChangeItemViewModelTests.cs
git commit -m "feat(vsix): 변경 상태를 화면에서만 한국어로 옮긴다"
```

---

## Task 2: 도구 창의 라벨과 문장을 한국어로 바꾼다

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:22`
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs:180` (주석)

**Interfaces:**
- Consumes: 태스크 1의 `StateText` 바인딩 (이미 적용됨 — 이 태스크는 그 컬럼의 **헤더**만 바꾼다)
- Produces: 없음 (화면 문구뿐)

**툴팁은 손대지 않는다.** 이미 전부 한국어다. `Connect`·`Refresh`·`Setup DBVC` 세 버튼에는 툴팁이 아예 없는데, **채우지 않는다** — 문구 통일과 별개의 일이고 이 계획의 범위 밖이다.

- [ ] **Step 1: 버튼 라벨과 폭을 바꾼다**

`ViewChangesControl.xaml`. **`Commit`·`Pull`·`Push` 는 건드리지 않는다.**

24행 (`Connect`), `Width` 를 80에서 70으로:
```xml
                <Button Content="연결" Command="{Binding ConnectCommand}" Width="70" Margin="0,0,10,4"
                        ToolTip="SSMS 개체 탐색기에서 선택한 데이터베이스로 접속합니다. 인증 정보는 그 연결에서만 오며 디스크에 저장되지 않습니다."/>
```

67행 (`Refresh`), `Width` 를 70에서 80으로:
```xml
                <Button Content="새로고침" Command="{Binding RefreshCommand}" Width="80" Margin="0,0,10,4"/>
```

76~79행 (스크립트 버튼 둘), `Width` 를 각각 100으로:
```xml
                <Button Content="배포 스크립트" Command="{Binding GenerateDeploymentScriptCommand}" Width="100" Margin="0,0,6,4"
                        ToolTip="선택한 객체의 현재 DDL을 단일 .sql 파일로 병합합니다." />
                <Button Content="롤백 스크립트" Command="{Binding GenerateRollbackScriptCommand}" Width="100" Margin="0,0,0,4"
                        ToolTip="선택한 객체가 마지막으로 커밋되기 직전 코드를 단일 .sql 파일로 병합합니다." />
```

142행 (`Setup DBVC`), `Width` 150 유지:
```xml
                <Button Content="DBVC 초기화" Command="{Binding SetupCommand}" Width="150" Height="40" FontSize="14" Cursor="Hand"/>
```

`저장소 연결...`(46행)은 **이미 한국어이므로 그대로 둔다.**

- [ ] **Step 2: 컬럼과 탭 헤더를 바꾼다**

86행·93행·94행 (변경 목록 컬럼). 93행의 `DisplayMemberBinding` 은 태스크 1에서 이미 `StateText` 로 바뀌어 있다 — 되돌리지 말 것:
```xml
                        <GridViewColumn Header="스테이징">
```
```xml
                        <GridViewColumn Header="상태" DisplayMemberBinding="{Binding StateText}"/>
                        <GridViewColumn Header="객체" DisplayMemberBinding="{Binding ObjectName}"/>
```

103행·117행 (탭):
```xml
                <TabItem Header="비교">
```
```xml
                <TabItem Header="이력">
```

122~125행 (이력 컬럼). **`SHA` 는 그대로 둔다:**
```xml
                                    <GridViewColumn Header="날짜" Width="130" DisplayMemberBinding="{Binding Date}"/>
                                    <GridViewColumn Header="작성자" Width="110" DisplayMemberBinding="{Binding Author}"/>
                                    <GridViewColumn Header="메시지" Width="320" DisplayMemberBinding="{Binding Message}"/>
                                    <GridViewColumn Header="SHA" Width="80" DisplayMemberBinding="{Binding ShortSha}"/>
```

- [ ] **Step 3: 초기화 안내문을 바꾼다**

141행:
```xml
                <TextBlock Text="이 데이터베이스는 아직 DBVC로 초기화되지 않았습니다." FontSize="16" Margin="0,0,0,20" Foreground="#333333"/>
```

- [ ] **Step 4: 경고 배너 문구를 바꾼다**

`ViewChangesViewModel.cs:22`:

```csharp
        // "매핑"은 ConfigManager의 내부 용어다. 바로 옆에 붙는 버튼이 "저장소 연결..."이므로
        // 배너도 같은 말을 써야 무엇을 눌러야 하는지 문장 하나로 전해진다.
        private const string NotMappedWarning = "현재 데이터베이스에 연결된 Git 저장소가 없습니다.";
```

- [ ] **Step 5: 테스트의 주석을 문구에 맞춘다**

`ViewChangesViewModelTests.cs:180`. 이 줄은 주석이며 검증하는 코드가 아니다:

```csharp
            // 설계: "현재 데이터베이스에 연결된 Git 저장소가 없습니다." 경고 표시 + 커밋 비활성화
```

- [ ] **Step 6: 빌드와 테스트를 확인한다**

Run: `dotnet build DBVC.slnx && dotnet test tests/DBVC.Vsix.Tests`
Expected: 빌드 0 errors, Vsix 156 passing, 0 failing

테스트가 실패하면 영어 문구를 검증하는 테스트가 남아 있다는 뜻이다. 그 테스트를 새 문구로 갱신하고 무엇이었는지 보고한다.

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 도구 창의 버튼·컬럼·탭·안내문을 한국어로 바꾼다"
```

---

## Task 3: 스크립트 대화상자 제목과 파일 필터를 한국어로 바꾼다

제목은 한국어로 가되 **기본 파일명은 ASCII로 남긴다.** 지금은 값 하나가 양쪽에 쓰이므로 둘로 갈라야 한다.

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`GenerateScript`, 약 734-753행)
- Modify: `src/DBVC.Vsix/Services/IFileSaveDialog.cs:21`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs:1401`

**Interfaces:**
- Consumes: 없음
- Produces: 알림·대화상자 제목이 `DBVC 배포 스크립트` / `DBVC 롤백 스크립트`. 기본 파일명은 `DBVC_Deployment_<DB>.sql` / `DBVC_Rollback_<DB>.sql` **로 유지**.

- [ ] **Step 1: 기존 테스트를 새 제목으로 고친다 (먼저 실패시킨다)**

`ViewChangesViewModelTests.cs:1401`:

```csharp
            Assert.That(_notifier.InfoCalls[0].Title, Is.EqualTo("DBVC 배포 스크립트"));
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~GenerateDeploymentScript"`
Expected: FAIL — 실제 값이 아직 `"DBVC Deployment Script"` 다.

> **주의:** 같은 파일 1334·1362행과 `ScriptGeneratorTests.cs` 30·43행도 `"DBVC Deployment Script"` / `"DBVC Rollback Script"` 를 검사하지만, 그것들은 **생성된 스크립트 본문의 헤더**를 보는 것이다. 그 헤더는 영어로 남기기로 했으므로 **건드리지 말 것.** 문자열이 같아서 헷갈리기 쉬운데, 이 태스크가 그 둘을 갈라놓는다 — 알림 제목만 바뀐다.

- [ ] **Step 3: 제목과 파일명을 가른다**

`ViewChangesViewModel.GenerateScript` 의 첫 두 줄을 바꾼다.

```csharp
            // 표시용과 파일명용을 가른다. 기본 파일명을 한글로 만들면 폐쇄망 반입이나
            // 다른 도구의 처리에서 인코딩 문제를 살 뿐이고, 얻는 것이 없다.
            var kindText = kind == ScriptKind.Rollback ? "롤백" : "배포";
            var kindSlug = kind == ScriptKind.Rollback ? "Rollback" : "Deployment";
            var title = $"DBVC {kindText} 스크립트";
```

그리고 저장 경로를 묻는 부분에서 파일명만 `kindSlug` 를 쓰게 한다:

```csharp
            var targetPath = _saveDialog.PromptForSavePath(
                $"{title} 저장",
                $"DBVC_{kindSlug}_{DatabaseName}.sql");
```

`title` 은 이 메서드 안에서 "내보낼 내용이 없습니다" 알림과 성공 알림의 제목으로도 쓰인다. 한 번 바꾸면 그 셋이 함께 한국어가 된다 — 추가 편집이 필요 없다.

- [ ] **Step 4: 파일 저장 필터를 바꾼다**

`IFileSaveDialog.cs:21`:

```csharp
                Filter = "SQL 스크립트 (*.sql)|*.sql|모든 파일 (*.*)|*.*",
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests && dotnet test tests/DBVC.Core.Tests`
Expected: Vsix 156 passing, Core 261 passing, 0 failing

Core가 그대로 통과해야 한다 — `ScriptGenerator` 를 건드리지 않았다는 증거다.

- [ ] **Step 6: 기본 파일명이 ASCII로 남았는지 눈으로 확인한다**

Run: `grep -n 'DBVC_' src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
Expected: `$"DBVC_{kindSlug}_{DatabaseName}.sql"` 한 줄. `kindText` 가 파일명 쪽에 들어가 있으면 잘못된 것이다.

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/Services/IFileSaveDialog.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 스크립트 대화상자 제목과 파일 필터를 한국어로 바꾼다"
```

---

## Task 4: 메뉴와 확장 정보를 한국어로 바꾼다

**Files:**
- Modify: `src/DBVC.Vsix/DbvcPackage.vsct` (46-47행)
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest` (5-6행)
- Modify: `src/DBVC.Vsix/Commands/CompareWithRepositoryCommand.cs:16` (XML 주석)

**Interfaces:**
- Consumes: 없음
- Produces: 없음

- [ ] **Step 1: 컨텍스트 메뉴 문구를 바꾼다**

`DbvcPackage.vsct` 의 `CompareWithRepositoryCommandId` 버튼. `ToolTipText` 는 이미 한국어이므로 그대로 둔다.

```xml
        <Strings>
          <ButtonText>DBVC: 저장소 버전과 비교</ButtonText>
          <MenuText>DBVC: 저장소 버전과 비교</MenuText>
          <ToolTipText>선택한 객체를 Git 저장소의 버전과 비교합니다.</ToolTipText>
        </Strings>
```

**위쪽 `ViewChangesCommandId` 버튼의 `DBVC` 는 제품 이름이므로 바꾸지 않는다.**

- [ ] **Step 2: 확장 이름과 설명을 바꾼다**

`source.extension.vsixmanifest`. **`Identity` 줄(`Version`·`Language` 포함)과 `<Tags>` 는 건드리지 않는다.**

```xml
    <DisplayName>DBVC — SSMS 데이터베이스 형상 관리</DisplayName>
    <Description xml:space="preserve">SQL Server Management Studio 21용 데이터베이스 형상 관리(Database Version Control) 확장입니다.</Description>
```

- [ ] **Step 3: 코드 주석의 메뉴 이름을 맞춘다**

`CompareWithRepositoryCommand.cs:16`. 화면에 뜨지는 않지만 다음 사람이 코드에서 메뉴를 찾을 때 쓰는 이름이다.

```csharp
    /// SQL 에디터 컨텍스트 메뉴의 "DBVC: 저장소 버전과 비교" 명령. (Feature 11, 12)
```

- [ ] **Step 4: 빌드가 되는지 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 0 errors

`.vsct` 는 XML이므로 오타가 나면 빌드가 깨진다. 빌드 통과가 이 태스크의 유일한 자동 검증이다 — 메뉴가 실제로 그렇게 뜨는지는 SSMS에서만 확인된다.

- [ ] **Step 5: 바꾸지 말아야 할 것이 그대로인지 확인한다**

Run: `grep -n 'Language=\|<Tags>\|Version=' src/DBVC.Vsix/source.extension.vsixmanifest`
Expected: `Language="en-US"`, `Version="1.2.0"`, `<Tags>SQL, SSMS, Git, Version Control, Database</Tags>` 가 그대로.

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/DbvcPackage.vsct src/DBVC.Vsix/source.extension.vsixmanifest src/DBVC.Vsix/Commands/CompareWithRepositoryCommand.cs
git commit -m "feat(vsix): 컨텍스트 메뉴와 확장 이름·설명을 한국어로 바꾼다"
```

---

## Task 5: 문서를 바뀐 화면에 맞춘다

문서가 없는 버튼을 누르라고 시키면 안 된다. 함께 "View Changes" 명칭도 정리한다.

**Files:**
- Modify: `README.md`
- Modify: `docs/setup-checklist.md`

**Interfaces:**
- Consumes: 태스크 1~4가 확정한 화면 문구
- Produces: 없음

- [ ] **Step 1: 바꿀 곳을 센다**

```bash
for t in "Setup DBVC" "Refresh" "Connect" "Deployment Script" "Rollback Script" "Diff" "History" "View Changes"; do
  printf "%-20s README:%s  checklist:%s\n" "$t" "$(grep -co "$t" README.md)" "$(grep -co "$t" docs/setup-checklist.md)"
done
```

착수 시점의 근사치: `Setup DBVC` 1/3, `Refresh` 3/8, `Connect` 3/15, `Deployment Script` 0/2, `Rollback Script` 0/1, `Diff` 3/5, `History` 2/2, `View Changes` 2/1.

- [ ] **Step 2: 라벨 참조를 바꾼다**

번역표:

| 문서의 현재 표기 | 바꿀 표기 |
| --- | --- |
| `Connect` | 연결 |
| `Refresh` | 새로고침 |
| `Setup DBVC` | DBVC 초기화 |
| `Deployment Script` | 배포 스크립트 |
| `Rollback Script` | 롤백 스크립트 |
| `Diff` (탭 이름일 때) | 비교 |
| `History` (탭 이름일 때) | 이력 |

**`Commit`·`Pull`·`Push` 는 손대지 않는다.** 화면에서 그대로 영어다.

**문맥을 보고 판단할 것.** 기계적 치환을 하면 안 된다.

* **탭 이름일 때만 바꾼다.** `하단 **Diff** 탭에서` → `하단 **비교** 탭에서`. 반면 `diff가 틀어진다`, `Diff 렌더링 엔진` (DiffPlex 설명), `좌우 분할(Side-by-Side) 뷰` 같은 일반 명사·제품명은 **그대로 둔다.**
* **버튼을 가리킬 때만 바꾼다.** 굵게 표시된 `**Refresh**` 는 버튼이다. `Refresh·Connect·Setup·Commit 직후에만` 처럼 동작을 나열하는 문장도 버튼 이름을 부르는 것이므로 함께 바꾼다.
* `History 탭` → `이력 탭`. `docs/superpowers/` 아래의 설계·계획 문서는 **건드리지 않는다** — 당시 상태의 기록이다.

- [ ] **Step 3: "View Changes" 명칭을 정리한다**

UI는 이 이름을 한 번도 말하지 않는다. 창 제목은 `DBVC` 이고, 그것을 여는 보기 메뉴 항목도 `DBVC` 다. **문서를 UI에 맞춘다.**

`README.md:9`
```markdown
- **WPF 기반 차이점 뷰어 (DBVC 창):**
```

`README.md:23`
```markdown
변경 상태는 DBVC 창에서 모두 확인할 수 있습니다.
```

`docs/setup-checklist.md:459` (줄 번호는 앞 단계의 편집으로 밀렸을 수 있다 — `View Changes` 로 찾을 것)
```markdown
  변경 상태는 DBVC 창에서 확인한다.
```

- [ ] **Step 4: 남은 것을 확인한다**

Step 1의 명령을 다시 돌린다.
Expected: `View Changes` 0/0. 나머지 항목에 남은 것이 있으면 그것이 일반 명사·제품명(`DiffPlex`, `diff`)인지 확인하고, 무엇을 왜 남겼는지 보고한다.

- [ ] **Step 5: 문서에 남은 영어 버튼 이름을 훑는다**

```bash
grep -nE '\*\*(Connect|Refresh|Setup DBVC|Deployment Script|Rollback Script|Diff|History)\*\*' README.md docs/setup-checklist.md
```
Expected: 결과 없음. 나오면 Step 2에서 놓친 것이다.

- [ ] **Step 6: 커밋**

```bash
git add README.md docs/setup-checklist.md
git commit -m "docs: 한국어로 바뀐 화면 문구에 문서를 맞춘다"
```

---

## 수동 검증 (구현 후, SSMS 21 실행 환경)

CI가 검증하지 않는 것들이다 — WPF 렌더링, `.vsct` 메뉴 등록, 확장 관리자 표시.

- [ ] `.vsix` 를 빌드해 SSMS 21에 설치한다. **확장 관리자에 `DBVC — SSMS 데이터베이스 형상 관리` 로 뜨는지** 확인한다.
- [ ] 보기 메뉴에서 DBVC 창을 연다. **버튼 라벨이 전부 한국어인지**(단 `Commit`·`Pull`·`Push` 는 영어), 컬럼·탭 헤더가 한국어인지 확인한다.
- [ ] **창을 좁게 도킹해 `WrapPanel` 줄바꿈을 본다.** 버튼 폭을 줄였으므로 이전보다 덜 접혀야 한다. 잘리는 버튼이 없어야 한다.
- [ ] 매핑되지 않은 데이터베이스에 접속해 **노란 경고 배너와 그 옆 `저장소 연결...` 버튼이 같은 말을 쓰는지** 확인한다.
- [ ] 초기화되지 않은 데이터베이스에서 **안내문과 `DBVC 초기화` 버튼이 같은 말을 쓰는지** 확인한다.
- [ ] 객체를 변경하고 새로고침해 **상태 컬럼에 `추가`·`수정`·`삭제` 가 뜨는지** 확인한다.
- [ ] SQL 에디터에서 객체 이름을 선택하고 우클릭해 **`DBVC: 저장소 버전과 비교`** 가 뜨는지 확인한다.
- [ ] 배포 스크립트를 생성한다. **대화상자 제목이 `DBVC 배포 스크립트 저장`, 필터가 `SQL 스크립트 (*.sql)`, 기본 파일명이 `DBVC_Deployment_<DB>.sql`(ASCII)** 인지 확인한다.
- [ ] 생성된 `.sql` 파일을 연다. **헤더는 영어(`DBVC Deployment Script`, `Generated:`)로 남아 있어야 한다.**
