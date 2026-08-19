# Pull 결과 구분 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Pull이 원격에서 실제로 커밋을 받았을 때와 이미 최신이었을 때를 구분해 안내하고, 받은 경우에는 스크립트가 놓인 저장소 폴더 경로를 함께 알린다.

**Architecture:** `GitManager.PullChanges`의 반환 타입을 `bool`에서 3상태 `PullResult` enum으로 바꿔 `MergeStatus.UpToDate`를 성공과 구분한다. `ViewChangesViewModel.Pull`은 이미 Push가 쓰는 것과 같은 모양의 `switch`로 문구를 가르고, 이력 재적재는 실제로 받았을 때만 한다. 새 판정 로직은 전부 Core에 있고 UI는 문구만 고른다.

**Tech Stack:** C# / .NET (Core는 netstandard2.0 + net48, Vsix는 net48 WPF), LibGit2Sharp, NUnit, Moq

**Spec:** `docs/superpowers/specs/2026-08-19-pull-result-distinction-design.md`

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 예외 메시지, 알림, 버튼, ToolTip 포함. Core는 상태를 영어 식별자(`PullResult.AlreadyUpToDate`)로 다루고, 한국어는 화면 계층에서만 만든다.
- **주석은 "왜"만 적는다.** 한국어 평서문으로, 함정과 근거를 남기는 기존 문체를 따른다.
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프**: `feat(core): 메모리 전용 자격증명 저장소를 더한다`.
- **테스트 이름은 영어** `Method_Result_WhenCondition` 형태다.
- **TDD**: 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **패키지 버전을 올리지 않는다.** `Microsoft.Data.SqlClient 5.1.5`, `Microsoft.SqlServer.SqlManagementObjects 171.30.0`은 SSMS 21에 맞춘 고정 값이다. 이 작업은 패키지를 건드릴 이유가 없다.
- **테스트 프로젝트에 MDS/SMO를 PackageReference 하지 않는다.** 전이 참조로만 받는다.
- 작업 브랜치는 `feature/pull-result-distinction`이다. 이미 만들어져 있고 설계 문서 커밋이 올라가 있다.
- 빌드·테스트 명령:
  ```bash
  dotnet build DBVC.slnx
  dotnet test tests/DBVC.Core.Tests -f net10.0
  dotnet test tests/DBVC.Vsix.Tests
  ```
  `DBVC.Vsix.Tests`는 net48 전용이라 Windows에서만 돈다. `SmoManagerIntegrationTests`가 Skip으로 나오는 것은 정상이다(로컬 SQL Server가 없으면 Skip이지 실패가 아니다).

---

### Task 1: Core가 "받을 것이 없었다"를 구분한다

`PullResult` enum을 만들고 `PullChanges`가 그것을 돌려주게 한다. `IGitManager`가 바뀌므로 Vsix 호출부와 Moq 설정도 이 태스크 안에서 기계적으로 맞춰 트리를 계속 빌드 가능하게 유지한다. 사용자에게 보이는 동작은 아직 바뀌지 않는다 — 그것은 Task 2다.

**Files:**
- Create: `src/DBVC.Core/Models/PullResult.cs`
- Modify: `src/DBVC.Core/GitManager.cs:195-254`
- Modify: `src/DBVC.Core/Abstractions.cs:61`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:546-556` (기계적 적응만)
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs:499-532`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` (Moq 설정 기계적 적응만)

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces:
  - `DBVC.Core.Models.PullResult` — `NoMapping` / `AlreadyUpToDate` / `Pulled` 세 값을 가진 enum
  - `PullResult IGitManager.PullChanges(string serverName, string databaseName)` — 기존 `bool` 시그니처를 대체한다. 던지는 예외(`MergeConflictException`, `WorkingTreeConflictException`, `GitAuthenticationException`, `GitRemoteException`, `InvalidOperationException`)는 그대로다.

- [x] **Step 1: `PullResult` enum 파일을 만든다**

`src/DBVC.Core/Models/PullResult.cs`를 새로 만든다. `PushResult.cs` 바로 옆이며, 주석 문체도 그것을 따른다.

```csharp
namespace DBVC.Core.Models
{
    /// <summary>
    /// Pull의 결과. 성공/실패 두 값으로는 "받을 것이 없었다"를 말할 수 없어
    /// 화면이 받은 것이 없는데 받았다고 안내하게 된다.
    /// </summary>
    public enum PullResult
    {
        /// <summary>이 (서버, 데이터베이스)에 매핑된 저장소가 없다.</summary>
        NoMapping,

        /// <summary>원격에 새 커밋이 없었다. 정상 상태이며 오류가 아니다.</summary>
        AlreadyUpToDate,

        /// <summary>원격의 커밋을 로컬에 반영했다.</summary>
        Pulled
    }
}
```

- [x] **Step 2: 시그니처를 바꾸고 두 반환문만 옮긴다 (UpToDate 분기는 아직 넣지 않는다)**

`src/DBVC.Core/Abstractions.cs:61`:

```csharp
        PullResult PullChanges(string serverName, string databaseName);
```

`src/DBVC.Core/GitManager.cs:195`의 선언을 바꾼다:

```csharp
        public PullResult PullChanges(string serverName, string databaseName)
```

198행:

```csharp
            if (repoPath == null) return PullResult.NoMapping;
```

253행:

```csharp
            return PullResult.Pulled;
```

`MergeStatus.Conflicts` 분기(245-251행)와 `catch` 다섯 개는 손대지 않는다 — 그 순서에 정확성이 걸려 있다는 주석이 이미 붙어 있다.

- [x] **Step 3: 호출부와 Moq 설정을 기계적으로 맞춘다**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:549`의 조건을 바꾼다. 동작은 이전과 같다(매핑 없음만 오류로 본다):

```csharp
                if (_gitManager.PullChanges(ServerName!, DatabaseName!) == PullResult.NoMapping)
```

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`에서 치환한다:
- `.Returns(true)` 6곳(851·865·978·993·1019·1037행) → `.Returns(PullResult.Pulled)`
- `.Returns(false)` 1곳(879행) → `.Returns(PullResult.NoMapping)`
- `.Throws(...)`로 끝나는 네 곳(907·924·940·961행)은 반환값을 쓰지 않으므로 그대로 둔다.

876행 테스트 이름도 반환값을 말하고 있으므로 함께 고친다:

```csharp
        public void PullCommand_ReportsAMissingMapping_WhenPullChangesReturnsNoMapping()
```

두 파일 모두 `using DBVC.Core.Models;`가 이미 있는지 확인하고, 없으면 더한다.

- [x] **Step 4: 빌드가 통과하는지 확인한다**

Run: `dotnet build DBVC.slnx`
Expected: 성공. 실패하면 `PullChanges`를 쓰는 곳이 더 있다는 뜻이므로 `grep -rn "PullChanges" --include=*.cs src tests`로 남은 곳을 찾는다.

- [x] **Step 5: 실패하는 테스트를 쓴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs`의 `PullChanges_FastForwards_WhenRemoteHasNewCommits` 바로 아래에 더한다.

```csharp
        [Test]
        public void PullChanges_ReturnsAlreadyUpToDate_WhenTheRemoteHasNoNewCommits()
        {
            // 원격에 새 커밋이 없으면 libgit2는 MergeStatus.UpToDate를 준다. 이것을
            // FastForward와 구분하지 않으면 화면이 받은 것이 없는데 받았다고 말한다.
            var originPath = NewRepoWithCommit();
            var clonePath = NewTempDir();
            Repository.Clone(originPath, clonePath);

            var git = NewGitManager("localhost", "testdb", clonePath);

            var result = git.PullChanges("localhost", "testdb");

            Assert.That(result, Is.EqualTo(PullResult.AlreadyUpToDate),
                "clone 직후에는 원격에 받아올 새 커밋이 없습니다");
        }
```

- [x] **Step 6: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PullChanges_ReturnsAlreadyUpToDate"`
Expected: FAIL. `Expected: AlreadyUpToDate / But was: Pulled`.

이 red가 나오지 않고 통과하거나 다른 이유로 실패하면 멈추고 원인을 본다. 여기서 통과하면 테스트가 아무것도 지키지 않는 것이다.

- [x] **Step 7: `UpToDate` 분기를 구현한다**

`src/DBVC.Core/GitManager.cs`의 253행(Step 2에서 `return PullResult.Pulled;`로 바꾼 줄)을 대체한다:

```csharp
            // UpToDate는 "받을 것이 없었다"이지 실패가 아니다. Pulled와 구분하지 않으면
            // 화면이 받은 것이 없는데 받았다고 말하고, 사용자는 받은 스크립트를 찾아 헤맨다.
            return result.Status == MergeStatus.UpToDate
                ? PullResult.AlreadyUpToDate
                : PullResult.Pulled;
```

`MergeStatus.NonFastForward`(병합 커밋이 만들어진 경우)는 `Pulled`로 남는다.

- [x] **Step 8: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~PullChanges_ReturnsAlreadyUpToDate"`
Expected: PASS

- [x] **Step 9: 기존 Core 테스트 두 개의 단언을 옮긴다**

`tests/DBVC.Core.Tests/GitManagerTests.cs:517-518` (`PullChanges_FastForwards_WhenRemoteHasNewCommits`):

```csharp
            Assert.That(result, Is.EqualTo(PullResult.Pulled));
```

`tests/DBVC.Core.Tests/GitManagerTests.cs:525-531` — 이름이 `false`를 말하므로 이름과 단언을 함께 고친다:

```csharp
        [Test]
        public void PullChanges_ReturnsNoMapping_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var git = new GitManager(new ConfigManager(configPath));

            Assert.That(git.PullChanges("localhost", "testdb"), Is.EqualTo(PullResult.NoMapping));
        }
```

파일 상단에 `using DBVC.Core.Models;`가 없으면 더한다.

- [x] **Step 10: 전체 테스트를 돌린다**

Run:
```bash
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests
```
Expected: 모두 PASS. 예외를 단언하는 나머지 Pull 테스트 여덟 개(`GitManagerTests.cs`의 534·564·583·613·631·662·1086·1144행)는 반환값을 보지 않으므로 그대로 통과해야 한다. `SmoManagerIntegrationTests`의 Skip은 정상이다.

- [x] **Step 11: 커밋한다**

```bash
git add src/DBVC.Core/Models/PullResult.cs src/DBVC.Core/GitManager.cs src/DBVC.Core/Abstractions.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Core.Tests/GitManagerTests.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(core): Pull 결과에서 이미 최신인 경우를 구분한다"
```

---

### Task 2: 화면이 결과에 맞는 문구를 고른다

Core가 구분해 준 세 상태를 사용자에게 옮긴다. 받았을 때만 저장소 경로를 알리고, 받았을 때만 이력을 다시 읽는다.

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs:546-587`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs:991-1001` (수정) 및 그 아래에 신규 두 개

**Interfaces:**
- Consumes: Task 1의 `PullResult`와 `IGitManager.PullChanges`
- Produces: 사용자에게 보이는 문구 세 가지. 뒤 태스크의 문서가 이 문구를 그대로 인용한다.
  - `AlreadyUpToDate` → `"원격에 새 변경이 없습니다. 저장소가 이미 최신입니다."`
  - `Pulled` → `"원격 저장소의 변경을 가져왔습니다."`로 시작해 `mapping.GitPath`를 포함하는 여러 줄
  - `NoMapping` → `"매핑된 Git 저장소를 찾을 수 없습니다."` (변경 없음)

- [x] **Step 1: 실패하는 테스트 세 개를 쓴다**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`의 기존 `PullCommand_NotifiesOnSuccess`(991행)를 아래로 **대체**하고, 이어서 새 테스트 두 개를 그 뒤에 더한다. 픽스처의 매핑은 `GitPath = @"C:\repo"`(59행)이다.

```csharp
        [Test]
        public void PullCommand_NotifiesOnSuccess_AndSaysWhereTheScriptsLanded()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Infos, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors, Is.Empty);
            Assert.That(_notifier.Infos[0], Does.Contain(@"C:\repo"),
                "받은 스크립트가 어디 놓였는지 말하지 않으면 사용자가 찾지 못합니다");
        }

        [Test]
        public void PullCommand_ReportsAlreadyUpToDate_WhenNothingWasPulled()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty, "받을 것이 없는 것은 오류가 아닙니다");
            Assert.That(_notifier.Infos, Has.Count.EqualTo(1));
            Assert.That(_notifier.Infos[0], Does.Not.Contain("가져왔습니다"),
                "받은 것이 없는데 가져왔다고 말하면 사용자가 없는 스크립트를 찾아 헤맵니다");
            Assert.That(_notifier.Infos[0], Does.Contain("이미 최신"));
        }

        [Test]
        public void PullCommand_DoesNotReloadHistory_WhenNothingWasPulled()
        {
            // 이력을 다시 읽어도 내용은 같다. 그런데 화면이 다시 그려지면 사용자는
            // 무언가 받아왔다고 읽는다 - 안내 문구를 고친 이유와 같은 문제다.
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };
            // SelectedChange 대입 자체가 이력을 한 번 읽는다. 그것을 세지 않도록 지운다.
            _git.Invocations.Clear();
            int selectionChangedCount = 0;
            vm.SelectionChanged += (_, __) => selectionChangedCount++;

            vm.PullCommand.Execute(null);

            _git.Verify(g => g.GetHistory(Server, Database, It.IsAny<string?>()), Times.Never,
                "받은 것이 없으면 이력이 바뀌지 않았으므로 다시 읽을 이유가 없습니다");
            Assert.That(selectionChangedCount, Is.EqualTo(0),
                "Diff도 다시 렌더링할 이유가 없습니다");
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~PullCommand_ReportsAlreadyUpToDate|FullyQualifiedName~PullCommand_DoesNotReloadHistory|FullyQualifiedName~PullCommand_NotifiesOnSuccess"`
Expected: 세 개 모두 FAIL.
- `NotifiesOnSuccess_AndSaysWhereTheScriptsLanded` — 안내에 `C:\repo`가 없다
- `ReportsAlreadyUpToDate_WhenNothingWasPulled` — 안내에 "가져왔습니다"가 들어 있다
- `DoesNotReloadHistory_WhenNothingWasPulled` — `GetHistory`가 1회 불렸다

- [x] **Step 3: `Pull()`의 성공 처리를 `switch`로 바꾼다**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`에서 `try { if (_gitManager.PullChanges(...) == PullResult.NoMapping) ... }` 블록부터 메서드 끝까지를 아래로 대체한다. `catch` 네 개(`MergeConflictException`, `WorkingTreeConflictException`, 그리고 마지막 포괄 `Exception`)는 지금 있는 주석까지 **그대로** 옮긴다 — 그 주석들은 되살리지 말라고 명시된 함정을 기록하고 있다.

```csharp
            PullResult result;
            try
            {
                result = _gitManager.PullChanges(ServerName!, DatabaseName!);
            }
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
            catch (Exception ex)
            {
                // 원인이 타입으로 갈렸으므로 흔한 원인을 추측해 덧붙이지 않는다.
                // GitAuthenticationException은 여기서 잡힌다 - Core가 이미 완전한 한국어
                // 안내를 메시지에 담아 던지므로, 전용 catch를 두면 이 분기와 완전히
                // 같은 코드를 중복할 뿐이다. 되살리지 말 것.
                _notifier.ShowError("DBVC Pull 실패", ex.Message);
                return;
            }

            // 여기서 Refresh를 부르면 안 된다. SMO 추출이 방금 받은 원격 변경을 즉시 덮어쓴다.
            switch (result)
            {
                case PullResult.NoMapping:
                    _notifier.ShowError("DBVC Pull 실패", "매핑된 Git 저장소를 찾을 수 없습니다.");
                    return;

                case PullResult.AlreadyUpToDate:
                    // 받은 것이 없으므로 이력도 Diff도 바뀌지 않았다. 아래 재적재를 건너뛴다 -
                    // 화면이 다시 그려지면 사용자는 무언가 받아왔다고 읽는다.
                    _notifier.ShowInfo("DBVC Pull", "원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.");
                    return;

                case PullResult.Pulled:
                    // 받은 스크립트가 어디 놓였는지 말하지 않으면 사용자가 찾지 못한다 -
                    // DBVC는 파일만 가져올 뿐 데이터베이스에 적용하지 않기 때문이다.
                    _notifier.ShowInfo(
                        "DBVC Pull",
                        "원격 저장소의 변경을 가져왔습니다." + Environment.NewLine +
                        "받은 스크립트는 아래 폴더에 있습니다:" + Environment.NewLine + Environment.NewLine +
                        mapping.GitPath + Environment.NewLine + Environment.NewLine +
                        "확인한 뒤 필요하면 데이터베이스에 적용하세요.");
                    break;
            }

            // History.Load와 SelectionChanged는 Git/작업 트리를 읽기만 할 뿐 SMO를 호출하지 않는다.
            // 그래서 위의 "Refresh 금지" 규칙과 충돌하지 않는다 — 오히려 Pull의 목적(새 커밋 반영)을
            // 이루려면 방금 받은 커밋 로그와 Diff를 화면에 즉시 보여줘야 한다.
            History.Load(ServerName, DatabaseName, SelectedChange?.RelativePath);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
```

`mapping`은 메서드 서두(527행)에서 이미 얻어 둔 지역 변수다. 새로 `TryGetMapping`을 부르지 않는다.

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests --filter "FullyQualifiedName~PullCommand"`
Expected: 모두 PASS. 특히 1003행 `PullCommand_ReloadsHistoryAndRendersDiff_AfterASuccessfulPull`이 계속 통과해야 한다 — 새 테스트와 짝을 이뤄 "받았을 때만 다시 읽는다"를 양쪽에서 지킨다.

- [x] **Step 5: 전체 테스트를 돌린다**

Run:
```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests
```
Expected: 모두 PASS

- [x] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): Pull 결과에 맞는 안내를 띄우고 받은 스크립트 위치를 알린다"
```

---

### Task 3: 문서와 버전을 실제 동작에 맞춘다

사용자 눈에 보이는 동작이 바뀌었으므로 `README.md`와 `docs/setup-checklist.md`를 고치고 확장 버전을 올린다. 체크리스트 297행은 **지금도 이미 틀린 안내**이며 이 변경 뒤에는 확실히 틀리게 된다.

**Files:**
- Modify: `README.md:60`
- Modify: `docs/setup-checklist.md:297`
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest:4`

**Interfaces:**
- Consumes: Task 2가 확정한 문구 세 가지
- Produces: 없음 (마지막 태스크)

- [x] **Step 1: `README.md`의 Pull 설명을 고친다**

60행의 `- **원격 변경 가져오기:**` 항목을 아래로 대체한다. 앞뒤 항목(59행, 61행)은 건드리지 않는다.

```markdown
- **원격 변경 가져오기:** **Pull** 은 원격의 변경을 로컬 저장소로 가져옵니다. **파일만 가져올 뿐 데이터베이스에 적용하지 않으므로**, 받은 스크립트를 확인한 뒤 필요하면 직접 실행하세요. 받아온 파일은 매핑된 저장소 폴더에 `[스키마]/[객체 유형]/[이름].sql` 로 놓이며, 알림이 그 폴더 경로를 함께 알려 줍니다. 원격에 새 변경이 없으면 `원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.` 로 알립니다. 커밋하지 않은 변경이 있으면 먼저 확인을 받습니다 — 병합 중 충돌이 나면 되돌리면서 그 변경도 함께 사라질 수 있기 때문입니다(새로고침으로 다시 추출할 수 있습니다).
```

- [x] **Step 2: `docs/setup-checklist.md`의 Pull 확인 항목을 고친다**

297행을 아래로 대체한다. 갓 설정한 저장소는 대개 받을 것이 없어 "가져왔습니다"가 뜨지 않는데, 지금 문장은 그 경우를 실패로 읽게 만든다.

```markdown
- [x] **Pull을 눌러본다.** `원격 저장소의 변경을 가져왔습니다.` 또는 `원격에 새 변경이 없습니다. 저장소가 이미 최신입니다.` 중 **어느 쪽이든** 알림이 뜨면 SSH 경로가 끝까지 동작하는 것이다. 갓 설정한 저장소는 받아올 커밋이 없으므로 대개 후자가 뜬다.
```

- [x] **Step 3: 확장 버전을 올린다**

`src/DBVC.Vsix/source.extension.vsixmanifest:4`의 `Identity` 요소에서 `Version="0.2.0"` → `Version="0.2.1"`. 같은 줄의 `Id`, `Language`, `Publisher`는 건드리지 않는다.

- [x] **Step 4: 문서에 남은 옛 서술이 없는지 훑는다**

Run: `grep -rn "가져왔습니다" README.md docs/`
Expected: Step 1·2에서 고친 곳과 설계 문서(`docs/superpowers/specs/`, `docs/superpowers/plans/`)만 나온다. 설계·계획 문서는 그 시점의 기록이므로 고치지 않는다. `README.md`나 `docs/setup-checklist.md`에 다른 곳이 더 나오면 같은 기준으로 고친다.

- [x] **Step 5: 빌드와 전체 테스트를 마지막으로 돌린다**

Run:
```bash
dotnet build DBVC.slnx
dotnet test tests/DBVC.Core.Tests -f net10.0
dotnet test tests/DBVC.Vsix.Tests
```
Expected: 모두 PASS

- [x] **Step 6: 커밋한다**

```bash
git add README.md docs/setup-checklist.md src/DBVC.Vsix/source.extension.vsixmanifest
git commit -m "docs: Pull 안내 문구 변경을 문서와 확장 버전에 반영한다"
```

---

## 완료 후 수동 확인 (CI가 검증하지 않는 영역)

CLAUDE.md가 명시하듯 WPF 렌더링과 SSMS 통합은 CI가 보지 않는다. 아래를 직접 눌러 보기 전에는 "동작한다"고 말할 수 없다.

- [x] `msbuild src/DBVC.Vsix/DBVC.Vsix.csproj -restore -p:Configuration=Release`로 `.vsix`를 만들고, `dir src\DBVC.Vsix\bin\Release\net48\*.vsix`로 **산출물이 실제로 생겼는지** 확인한다(빌드 성공 ≠ .vsix 생성).
- [x] SSMS 21에 설치하고 Pull을 두 번 누른다. 첫 번째(받을 것이 있을 때)는 저장소 경로가 실린 안내가, 두 번째는 `원격에 새 변경이 없습니다.`가 떠야 한다.
- [x] 두 번째 Pull 뒤에 이력 탭이 다시 그려지지 않는지 본다.
