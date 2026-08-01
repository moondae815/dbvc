# DBVC P1 결함 수정 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 설계에는 명시되어 있으나 실제로 동작하지 않는 결함 3건(삭제 객체 파일 정리, 매핑 등록 UI, Diff 하이라이팅)을 구현한다.

**Architecture:** 세 결함은 서로 독립적이다. 삭제 정리는 새 코어 컴포넌트 `WorkingTreeCleaner`를 만들어 `ViewChangesViewModel.Refresh`에 연결한다. 매핑 등록은 기존 `IFileSaveDialog` 패턴을 따르는 `IFolderBrowseDialog` 추상화 뒤에서 `ConnectRepositoryCommand`로 처리한다. Diff 하이라이팅은 순수 변환기 `DiffTextBuilder`와 AvalonEdit 배경 렌더러로 나눠, 테스트 가능한 부분을 WPF에서 분리한다.

**Tech Stack:** C# / .NET Framework 4.8 + .NET Standard 2.0, WPF, AvalonEdit 6.3, DiffPlex 1.7.2, LibGit2Sharp 0.32, NUnit 4, Moq

## Global Constraints

- `DBVC.Core`는 `net48;netstandard2.0` 멀티타깃, `DBVC.Vsix`는 `net48` 단일 타깃이다. Core에 WPF·VS SDK 의존성을 추가하지 않는다.
- 모든 프로젝트가 `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`이다.
- 저장소 상대 경로 규약은 `[Schema]/[ObjectType]/[ObjectName].sql`이며 구분자는 항상 슬래시(`/`)다. 경로 조립·해석은 반드시 `ObjectPathConvention`을 거친다.
- 코드 주석과 커밋 메시지 본문은 한국어, 테스트 메서드 이름은 영어 서술형(`Method_DoesSomething_WhenCondition`)이다. 기존 파일의 관례를 그대로 따른다.
- 테스트는 NUnit 4 + Moq다. 코어의 파일·Git 테스트는 실제 임시 폴더를 만드는 integration-style로 쓴다(`GitManagerTests` 참고).
- **macOS·Linux에서는 `net10.0` 타깃만 실행할 수 있다.** `Microsoft.Data.SqlClient`가 net462 구현체를 `runtimes/win` 아래에만 배포하기 때문이다. 이 계획의 테스트 명령은 모두 `-f net10.0`을 붙인다. Windows에서는 프레임워크 지정을 빼면 net48까지 함께 돈다.
- 커밋 메시지 제목 형식은 기존 이력을 따른다: `feat(core):`, `feat(vsix):`, `fix(core):`, `docs:`.

---

## File Structure

| 파일 | 책임 | 태스크 |
| --- | --- | --- |
| `src/DBVC.Core/WorkingTreeCleaner.cs` (신규) | DROP된 객체의 `.sql` 파일 제거 + `CleanupResult` | 1 |
| `src/DBVC.Core/Abstractions.cs` (수정) | `IWorkingTreeCleaner` 추가, `IGitManager.IsRepository` 추가 | 1, 3 |
| `src/DBVC.Core/GitManager.cs` (수정) | `IsRepository` 공개 | 3 |
| `src/DBVC.Vsix/Services/IFolderBrowseDialog.cs` (신규) | 폴더 선택 추상화 + WinForms 어댑터 | 4 |
| `src/DBVC.Vsix/Services/DiffTextBuilder.cs` (신규) | DiffPlex 결과 → 텍스트 + 줄 종류 (순수 변환) | 6 |
| `src/DBVC.Vsix/UI/DiffLineBackgroundRenderer.cs` (신규) | AvalonEdit 줄 배경 렌더링 | 7 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (수정) | 정리 호출, `ConnectRepositoryCommand` | 2, 4 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (수정) | 경고 배너 승격, 저장소 연결 버튼 | 5 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` (수정) | Diff 모델 연결, 스크롤 동기화 | 7 |
| `src/DBVC.Vsix/DBVC.Vsix.csproj` (수정) | `System.Windows.Forms` 참조 | 4 |

---

## Task 1: `WorkingTreeCleaner` (DBVC.Core)

**Files:**
- Create: `src/DBVC.Core/WorkingTreeCleaner.cs`
- Modify: `src/DBVC.Core/Abstractions.cs`
- Test: `tests/DBVC.Core.Tests/WorkingTreeCleanerTests.cs` (신규)

**Interfaces:**
- Consumes: `ChangeRecord`(`State`, `RelativePath`, `QualifiedName`, `LastLogId`), `ObjectPathConvention.TryParseRelativePath(string?, out string, out string, out string)`
- Produces:
  - `interface IWorkingTreeCleaner { CleanupResult RemoveDeletedObjectFiles(string repoPath, IEnumerable<ChangeRecord> records); }`
  - `class CleanupResult { List<string> RemovedPaths { get; } List<string> FailedPaths { get; } bool HasFailures { get; } }`
  - `class WorkingTreeCleaner : IWorkingTreeCleaner`

**배경:** `SmoManager`는 현재 존재하는 객체만 파일로 쓰고, 사라진 객체의 파일은 그대로 둔다. 그래서 `DROP TABLE dbo.Users` 이후에도 `dbo/Tables/Users.sql`이 작업 트리에 남아 Git이 삭제를 감지하지 못하고, 커밋이 "변경사항 없음"으로 끝난다.

- [ ] **Step 1: 인터페이스를 `Abstractions.cs`에 추가**

`src/DBVC.Core/Abstractions.cs`의 `ISmoManager` 선언 아래(파일 끝 `}` 두 개 직전)에 추가한다.

```csharp
    /// <summary>
    /// 작업 트리를 데이터베이스의 현재 상태에 맞춘다.
    /// DROP된 객체의 파일이 남아 있으면 Git이 삭제를 감지하지 못한다.
    /// </summary>
    public interface IWorkingTreeCleaner
    {
        CleanupResult RemoveDeletedObjectFiles(string repoPath, IEnumerable<ChangeRecord> records);
    }
```

- [ ] **Step 2: 실패하는 테스트를 작성**

`tests/DBVC.Core.Tests/WorkingTreeCleanerTests.cs`를 새로 만든다.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class WorkingTreeCleanerTests
    {
        private string _repoPath = null!;

        [SetUp]
        public void SetUp()
        {
            _repoPath = Path.Combine(Path.GetTempPath(), "dbvc_clean_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_repoPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (!Directory.Exists(_repoPath)) return;
            try { Directory.Delete(_repoPath, true); } catch { }
        }

        private string WriteFile(string relativePath, string content = "CREATE TABLE Users (Id INT);")
        {
            var full = Path.Combine(_repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return full;
        }

        private static ChangeRecord Record(string state, string relativePath, long lastLogId)
            => new ChangeRecord
            {
                Schema = "dbo",
                ObjectName = "Users",
                State = state,
                QualifiedName = "dbo.Users",
                RelativePath = relativePath,
                LastLogId = lastLogId
            };

        // ---------- 삭제 대상 ----------

        [Test]
        public void RemoveDeletedObjectFiles_DeletesTheFile_WhenADdlLogRowBacksTheDeletion()
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            var result = new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(File.Exists(full), Is.False,
                "파일이 남으면 Git이 삭제를 감지하지 못해 커밋되지 않습니다");
            Assert.That(result.RemovedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
            Assert.That(result.HasFailures, Is.False);
        }

        [Test]
        public void RemoveDeletedObjectFiles_IsCaseInsensitiveForTheState()
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(File.Exists(full), Is.False);
        }

        // ---------- 건드리면 안 되는 것 ----------

        [Test]
        public void RemoveDeletedObjectFiles_LeavesTheFile_WhenNoDdlLogRowBacksTheDeletion()
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            var result = new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Users.sql", 0) });

            Assert.That(File.Exists(full), Is.True,
                "LastLogId가 0이면 Git 상태에서만 유래한 항목이라 지울 근거가 없습니다");
            Assert.That(result.RemovedPaths, Is.Empty);
        }

        [TestCase("Modified")]
        [TestCase("Added")]
        public void RemoveDeletedObjectFiles_LeavesTheFile_ForStatesOtherThanDeleted(string state)
        {
            var full = WriteFile("dbo/Tables/Users.sql");

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record(state, "dbo/Tables/Users.sql", 7) });

            Assert.That(File.Exists(full), Is.True);
        }

        [TestCase("notes.txt")]
        [TestCase("dbo/Tables/extra/Users.sql")]
        [TestCase("Users.sql")]
        public void RemoveDeletedObjectFiles_LeavesFilesThatDoNotFollowThePathConvention(string relativePath)
        {
            var full = WriteFile(relativePath);

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", relativePath, 7) });

            Assert.That(File.Exists(full), Is.True,
                "규약에 맞지 않는 경로는 DBVC가 만든 파일이 아닙니다");
        }

        [Test]
        public void RemoveDeletedObjectFiles_NeverEscapesTheRepositoryRoot()
        {
            // 저장소를 한 단계 깊이 두고 그 형제 폴더에 희생양을 만든다.
            // 그래야 "../Tables/Secret.sql"이 실제로 그 파일을 가리킨다.
            var outer = Path.Combine(Path.GetTempPath(), "dbvc_escape_" + Guid.NewGuid().ToString("N"));
            var repo = Path.Combine(outer, "repo");
            var victim = Path.Combine(outer, "Tables", "Secret.sql");
            Directory.CreateDirectory(repo);
            Directory.CreateDirectory(Path.GetDirectoryName(victim)!);
            File.WriteAllText(victim, "secret");

            try
            {
                // ".." 세 조각도 경로 규약 검사는 통과하므로 루트 검사가 마지막 방어선이다.
                var result = new WorkingTreeCleaner()
                    .RemoveDeletedObjectFiles(repo, new[] { Record("Deleted", "../Tables/Secret.sql", 7) });

                Assert.That(File.Exists(victim), Is.True, "저장소 밖의 파일을 지워서는 안 됩니다");
                Assert.That(result.RemovedPaths, Is.Empty);
            }
            finally
            {
                try { Directory.Delete(outer, true); } catch { }
            }
        }

        // ---------- 무해한 입력 ----------

        [Test]
        public void RemoveDeletedObjectFiles_DoesNothing_WhenTheFileIsAlreadyGone()
        {
            var result = new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Gone.sql", 7) });

            Assert.That(result.RemovedPaths, Is.Empty);
            Assert.That(result.HasFailures, Is.False);
        }

        [Test]
        public void RemoveDeletedObjectFiles_LeavesTheDirectoryInPlace()
        {
            WriteFile("dbo/Tables/Users.sql");

            new WorkingTreeCleaner()
                .RemoveDeletedObjectFiles(_repoPath, new[] { Record("Deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(Directory.Exists(Path.Combine(_repoPath, "dbo", "Tables")), Is.True,
                "Git은 빈 디렉터리를 추적하지 않으므로 지울 이유가 없습니다");
        }

        [Test]
        public void RemoveDeletedObjectFiles_ReturnsEmpty_ForAMissingRepositoryPath()
        {
            var result = new WorkingTreeCleaner().RemoveDeletedObjectFiles(
                Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N")),
                new[] { Record("Deleted", "dbo/Tables/Users.sql", 7) });

            Assert.That(result.RemovedPaths, Is.Empty);
            Assert.That(result.HasFailures, Is.False);
        }

        [Test]
        public void RemoveDeletedObjectFiles_ToleratesNullCollectionAndNullRecords()
        {
            var cleaner = new WorkingTreeCleaner();

            Assert.DoesNotThrow(() => cleaner.RemoveDeletedObjectFiles(_repoPath, null!));
            Assert.DoesNotThrow(() => cleaner.RemoveDeletedObjectFiles(_repoPath, new ChangeRecord?[] { null }!));
        }

        // ---------- 실패 격리 ----------

        [Test]
        [Platform("Win", Reason = "읽기 전용 파일의 삭제가 거부되는 것은 Windows 동작입니다")]
        public void RemoveDeletedObjectFiles_IsolatesAFailure_AndKeepsProcessingTheRest()
        {
            var locked = WriteFile("dbo/Tables/Locked.sql");
            var deletable = WriteFile("dbo/Tables/Users.sql");
            File.SetAttributes(locked, FileAttributes.ReadOnly);

            try
            {
                var result = new WorkingTreeCleaner().RemoveDeletedObjectFiles(_repoPath, new[]
                {
                    Record("Deleted", "dbo/Tables/Locked.sql", 7),
                    Record("Deleted", "dbo/Tables/Users.sql", 8)
                });

                Assert.That(result.FailedPaths, Is.EqualTo(new[] { "dbo/Tables/Locked.sql" }));
                Assert.That(result.RemovedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }),
                    "하나의 실패가 나머지 정리를 막아서는 안 됩니다");
                Assert.That(result.HasFailures, Is.True);
                Assert.That(File.Exists(deletable), Is.False);
            }
            finally
            {
                File.SetAttributes(locked, FileAttributes.Normal);
            }
        }
    }
}
```

- [ ] **Step 3: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~WorkingTreeCleanerTests"
```

Expected: 컴파일 실패 — `WorkingTreeCleaner` 형식을 찾을 수 없음(CS0246)

- [ ] **Step 4: `WorkingTreeCleaner`를 구현**

`src/DBVC.Core/WorkingTreeCleaner.cs`를 새로 만든다.

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// DDL 로그가 DROP을 기록한 객체의 <c>.sql</c> 파일을 작업 트리에서 제거한다.
    /// SmoManager는 존재하는 객체만 추출하므로 사라진 객체의 파일은 아무도 지우지 않는다.
    /// 파일이 남으면 Git이 삭제를 감지하지 못해 커밋되지 않는다.
    /// </summary>
    public class WorkingTreeCleaner : IWorkingTreeCleaner
    {
        private const string DeletedState = "Deleted";

        public CleanupResult RemoveDeletedObjectFiles(string repoPath, IEnumerable<ChangeRecord> records)
        {
            var result = new CleanupResult();

            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath)) return result;

            var repoRoot = Path.GetFullPath(repoPath);

            foreach (var record in records ?? Enumerable.Empty<ChangeRecord>())
            {
                var fullPath = ResolveDeletableFile(repoRoot, record);
                if (fullPath == null) continue;

                try
                {
                    File.Delete(fullPath);
                    result.RemovedPaths.Add(record.RelativePath);
                }
                catch (Exception ex)
                {
                    // 파일 하나의 실패가 나머지 정리를 막아서는 안 된다. (SmoManager.ScriptAll과 같은 방침)
                    Debug.WriteLine($"WorkingTreeCleaner failed to delete '{record.RelativePath}': {ex.Message}");
                    result.FailedPaths.Add(record.RelativePath);
                }
            }

            return result;
        }

        /// <summary>
        /// 삭제해도 되는 파일이면 절대 경로를, 아니면 <c>null</c>을 반환한다.
        /// </summary>
        private static string? ResolveDeletableFile(string repoRoot, ChangeRecord? record)
        {
            if (record == null) return null;
            if (!string.Equals(record.State, DeletedState, StringComparison.OrdinalIgnoreCase)) return null;

            // DDL 로그에 근거가 있는 항목만 지운다.
            // LastLogId가 0이면 Git 상태에서만 유래한 항목이고, 그건 이미 파일이 없다는 뜻이다.
            if (record.LastLogId <= 0) return null;

            // DBVC의 경로 규약을 따르지 않는 파일은 DBVC가 만든 것이 아니다.
            if (!ObjectPathConvention.TryParseRelativePath(record.RelativePath, out _, out _, out _)) return null;

            var combined = Path.GetFullPath(
                Path.Combine(repoRoot, record.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            // ".." 세 조각도 경로 규약 검사는 통과하므로 루트 검사가 마지막 방어선이다.
            if (!IsUnder(repoRoot, combined)) return null;

            return File.Exists(combined) ? combined : null;
        }

        private static bool IsUnder(string root, string candidate)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 작업 트리 정리 결과. 실패한 경로는 사용자에게 알려야 한다.
    /// </summary>
    public class CleanupResult
    {
        public List<string> RemovedPaths { get; } = new List<string>();
        public List<string> FailedPaths { get; } = new List<string>();
        public bool HasFailures => FailedPaths.Count > 0;
    }
}
```

- [ ] **Step 5: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~WorkingTreeCleanerTests"
```

Expected: PASS. macOS·Linux에서는 `IsolatesAFailure` 테스트가 `[Platform("Win")]`로 건너뛰어진다(Skipped 1).

- [ ] **Step 6: 전체 테스트로 회귀 확인**

```bash
dotnet test DBVC.slnx -f net10.0
```

Expected: 기존 219개 + 신규 테스트 전부 통과, 실패 0

- [ ] **Step 7: 커밋**

```bash
git add src/DBVC.Core/WorkingTreeCleaner.cs src/DBVC.Core/Abstractions.cs tests/DBVC.Core.Tests/WorkingTreeCleanerTests.cs
git commit -m "feat(core): DROP된 객체의 작업 트리 파일을 정리하는 WorkingTreeCleaner

DDL 로그에 DROP이 기록된 객체만, 경로 규약과 저장소 루트 검사를 통과한
파일에 한해 지운다. 개별 실패는 격리해 FailedPaths로 보고한다."
```

---

## Task 2: `Refresh`에 작업 트리 정리 연결

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1의 `IWorkingTreeCleaner.RemoveDeletedObjectFiles(string, IEnumerable<ChangeRecord>)`, `CleanupResult.FailedPaths`, `CleanupResult.HasFailures`
- Produces: `ViewChangesViewModel` 생성자의 7번째 매개변수 `IWorkingTreeCleaner? cleaner = null`. Task 4가 그 뒤에 8번째 매개변수를 추가한다.

- [ ] **Step 1: 테스트 픽스처에 Mock을 추가**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`의 필드 선언부(`private RecordingSaveDialog _saveDialog = null!;` 다음 줄)에 추가한다.

```csharp
        private Mock<IWorkingTreeCleaner> _cleaner = null!;
```

`SetUp` 메서드의 `_smo.Setup(...)` 다음에 추가한다.

```csharp
            _cleaner = new Mock<IWorkingTreeCleaner>();
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(new CleanupResult());
```

`NewViewModel()`을 다음으로 교체한다.

```csharp
        private ViewChangesViewModel NewViewModel()
        {
            return new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog, _cleaner.Object);
        }
```

- [ ] **Step 2: 실패하는 테스트를 작성**

같은 파일에서 `// ---------- Commit ----------` 주석 바로 앞에 추가한다.

```csharp
        // ---------- 삭제된 객체의 작업 트리 정리 ----------

        [Test]
        public void Refresh_RemovesWorkingTreeFilesForDroppedObjects()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database))
                .Returns(new List<ChangeRecord> { Record("dbo", "Users", "Deleted", "dbo/Tables/Users.sql") });

            NewConnectedViewModel();

            _cleaner.Verify(
                c => c.RemoveDeletedObjectFiles(
                    @"C:\repo",
                    It.Is<IEnumerable<ChangeRecord>>(records => records.Any(r => r.RelativePath == "dbo/Tables/Users.sql"))),
                Times.AtLeastOnce,
                "파일이 남으면 Git이 삭제를 감지하지 못해 커밋되지 않습니다");
        }

        [Test]
        public void Refresh_WarnsWhenADroppedObjectFileCannotBeRemoved()
        {
            var failed = new CleanupResult();
            failed.FailedPaths.Add("dbo/Tables/Users.sql");
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(failed);

            var vm = NewConnectedViewModel();

            Assert.That(vm.WarningMessage, Does.Contain("dbo/Tables/Users.sql"));
        }

        [Test]
        public void Refresh_DoesNotWarn_WhenNothingFailedToBeRemoved()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.WarningMessage, Is.Null);
        }
```

- [ ] **Step 3: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: 컴파일 실패 — `ViewChangesViewModel` 생성자가 7개 인수를 받지 않음(CS1729)

- [ ] **Step 4: ViewModel에 정리 단계를 추가**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`에서 세 곳을 고친다.

필드 선언부의 `private readonly ScriptExporter _scriptExporter;` 앞에 추가:

```csharp
        private readonly IWorkingTreeCleaner _cleaner;
```

생성자 시그니처와 초기화:

```csharp
        public ViewChangesViewModel(
            IConfigManager configManager,
            IStateTracker? stateTracker,
            IGitManager? gitManager,
            ISmoManager? smoManager,
            IUserNotifier? notifier,
            IFileSaveDialog? saveDialog = null,
            IWorkingTreeCleaner? cleaner = null)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _gitManager = gitManager ?? new GitManager(_configManager);
            _stateTracker = stateTracker ?? new StateTracker(_configManager, _gitManager);
            _smoManager = smoManager ?? new SmoManager(_configManager);
            _notifier = notifier ?? new MessageBoxNotifier();
            _saveDialog = saveDialog ?? new SaveFileDialogAdapter();
            _cleaner = cleaner ?? new WorkingTreeCleaner();
            _scriptExporter = new ScriptExporter(_configManager, _gitManager);
```

(생성자 나머지 — `RefreshCommand = ...` 이하 — 는 그대로 둔다.)

`Refresh()`의 `_lastChangeRecords = _stateTracker.GetPendingChanges(...)` 줄과 그 아래 `foreach` 사이에 삽입:

```csharp
                _lastChangeRecords = _stateTracker.GetPendingChanges(ServerName!, DatabaseName!);

                // DROP된 객체의 파일을 지워야 Git이 삭제를 감지하고 커밋에 포함할 수 있다.
                // RefreshState가 Git 상태를 읽은 뒤이므로 이 정리가 목록 판정을 바꾸지 않는다.
                var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
                if (mapping != null)
                {
                    var cleanup = _cleaner.RemoveDeletedObjectFiles(mapping.GitPath, _lastChangeRecords);
                    if (cleanup.HasFailures)
                    {
                        warnings.Add($"삭제된 객체의 파일을 지우지 못했습니다: {string.Join(", ", cleanup.FailedPaths)}");
                    }
                }

                foreach (var record in _lastChangeRecords)
```

- [ ] **Step 5: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: PASS (신규 3개 포함, 기존 테스트 회귀 없음)

- [ ] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "fix(vsix): Refresh가 DROP된 객체의 작업 트리 파일을 정리

파일이 남아 있어 Git이 삭제를 감지하지 못하던 문제를 고친다.
정리 실패는 경고 배너로 알리고 나머지 새로고침은 계속 진행한다."
```

---

## Task 3: `IGitManager.IsRepository`

**Files:**
- Modify: `src/DBVC.Core/Abstractions.cs`
- Modify: `src/DBVC.Core/GitManager.cs`
- Test: `tests/DBVC.Core.Tests/GitManagerTests.cs`

**Interfaces:**
- Produces: `bool IGitManager.IsRepository(string path)` — Task 4의 매핑 등록이 저장할 폴더를 검증할 때 쓴다.

- [ ] **Step 1: 실패하는 테스트를 작성**

`tests/DBVC.Core.Tests/GitManagerTests.cs`에서 `// ---------- GetChangedFiles ----------` 주석 바로 앞에 추가한다.

```csharp
        // ---------- IsRepository ----------

        [Test]
        public void IsRepository_ReturnsTrue_ForAnInitializedRepository()
        {
            Assert.That(new GitManager().IsRepository(NewRepoWithCommit()), Is.True);
        }

        [Test]
        public void IsRepository_ReturnsFalse_ForAPlainDirectory()
        {
            var path = NewTempDir();
            Directory.CreateDirectory(path);

            Assert.That(new GitManager().IsRepository(path), Is.False,
                "git init되지 않은 폴더를 매핑하면 이후 모든 동작이 조용히 실패합니다");
        }

        [Test]
        public void IsRepository_ReturnsFalse_ForAMissingPath()
        {
            Assert.That(
                new GitManager().IsRepository(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N"))),
                Is.False);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerTests.IsRepository"
```

Expected: 컴파일 실패 — `GitManager`에 `IsRepository` 정의가 없음(CS1061)

- [ ] **Step 3: 인터페이스와 구현을 추가**

`src/DBVC.Core/Abstractions.cs`의 `IGitManager` 안, `string GetStatus(string repoPath);` 앞에 추가:

```csharp
        bool IsRepository(string path);
```

`src/DBVC.Core/GitManager.cs`의 `GetStatus` 메서드 앞에 추가:

```csharp
        /// <summary>
        /// 해당 경로가 유효한 Git 저장소인지 확인한다. 매핑 등록 전 검증에 쓴다.
        /// </summary>
        public bool IsRepository(string path) => IsValidRepository(path);
```

- [ ] **Step 4: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Core.Tests -f net10.0 --filter "FullyQualifiedName~GitManagerTests"
```

Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Core/Abstractions.cs src/DBVC.Core/GitManager.cs tests/DBVC.Core.Tests/GitManagerTests.cs
git commit -m "feat(core): IGitManager에 IsRepository 노출

매핑 등록 전 선택한 폴더가 Git 저장소인지 검증하기 위해
이미 존재하던 비공개 검사를 공개한다."
```

---

## Task 4: 매핑 등록 명령 (`ConnectRepositoryCommand`)

**Files:**
- Create: `src/DBVC.Vsix/Services/IFolderBrowseDialog.cs`
- Modify: `src/DBVC.Vsix/DBVC.Vsix.csproj`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: Task 3의 `IGitManager.IsRepository(string)`, `IConfigManager.AddMapping(string, string, string)`
- Produces:
  - `interface IFolderBrowseDialog { string? PromptForFolder(string description, string? initialPath); }`
  - `ViewChangesViewModel.ConnectRepositoryCommand` (`ICommand`)
  - `ViewChangesViewModel` 생성자의 8번째 매개변수 `IFolderBrowseDialog? folderDialog = null`

**배경:** `AddMapping`/`RemoveMapping`은 코어와 테스트에서만 호출된다. UI에 등록 경로가 없어 사용자는 `%APPDATA%\DBVC\mappings.json`을 손으로 만들어야 하고, 그전까지 플러그인은 아무 일도 하지 못한다.

- [ ] **Step 1: 폴더 선택 추상화를 작성**

`src/DBVC.Vsix/Services/IFolderBrowseDialog.cs`를 새로 만든다.

```csharp
namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 폴더를 선택받는다. ViewModel이 대화상자 구현에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IFolderBrowseDialog
    {
        /// <summary>사용자가 선택한 폴더 경로. 취소하면 <c>null</c>.</summary>
        string? PromptForFolder(string description, string? initialPath);
    }

    /// <summary>
    /// net48 WPF에는 폴더 선택 대화상자가 없어 Windows Forms의 것을 쓴다.
    /// </summary>
    public class FolderBrowserDialogAdapter : IFolderBrowseDialog
    {
        public string? PromptForFolder(string description, string? initialPath)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = false,
                SelectedPath = initialPath ?? string.Empty
            };

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }
    }
}
```

- [ ] **Step 2: csproj에 `System.Windows.Forms` 참조를 추가**

`src/DBVC.Vsix/DBVC.Vsix.csproj`의 프레임워크 어셈블리 `ItemGroup`에 한 줄 추가한다.

```xml
  <ItemGroup>
    <Reference Include="PresentationFramework" />
    <Reference Include="PresentationCore" />
    <Reference Include="WindowsBase" />
    <Reference Include="System.Xaml" />
    <Reference Include="System.Windows.Forms" />
  </ItemGroup>
```

- [ ] **Step 3: 실패하는 테스트를 작성**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`의 필드 선언부에 추가:

```csharp
        private RecordingFolderDialog _folderDialog = null!;
```

`SetUp`의 첫 줄 `_saveDialog = new RecordingSaveDialog();` 다음에 추가:

```csharp
            _folderDialog = new RecordingFolderDialog();
```

`NewViewModel()`을 다음으로 교체:

```csharp
        private ViewChangesViewModel NewViewModel()
        {
            return new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _folderDialog);
        }
```

파일 끝의 `RecordingNotifier` 클래스 뒤에 테스트 더블을 추가:

```csharp
        private sealed class RecordingFolderDialog : IFolderBrowseDialog
        {
            public string? PathToReturn { get; set; }
            public int CallCount { get; private set; }

            public string? PromptForFolder(string description, string? initialPath)
            {
                CallCount++;
                return PathToReturn;
            }
        }
```

`// ---------- Commit ----------` 주석 바로 앞에 테스트를 추가:

```csharp
        // ---------- 저장소 매핑 등록 ----------

        [Test]
        public void ConnectRepositoryCommand_IsEnabled_OnlyWhenTheDatabaseIsNotYetMapped()
        {
            var mapped = NewConnectedViewModel();
            Assert.That(mapped.ConnectRepositoryCommand.CanExecute(null), Is.False,
                "이미 매핑되어 있으면 저장소를 다시 연결할 이유가 없습니다");

            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            var unmapped = NewConnectedViewModel();
            Assert.That(unmapped.ConnectRepositoryCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void ConnectRepositoryCommand_SavesTheMapping_WhenTheChosenFolderIsAGitRepository()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _git.Setup(g => g.IsRepository(@"C:\chosen-repo")).Returns(true);
            _folderDialog.PathToReturn = @"C:\chosen-repo";
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(Server, Database, @"C:\chosen-repo"), Times.Once);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNothing_WhenTheUserCancels()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _folderDialog.PathToReturn = null;
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(_folderDialog.CallCount, Is.EqualTo(1));
            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Empty, "취소는 오류가 아닙니다");
        }

        [Test]
        public void ConnectRepositoryCommand_RefusesAFolderThatIsNotAGitRepository()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _git.Setup(g => g.IsRepository(It.IsAny<string>())).Returns(false);
            _folderDialog.PathToReturn = @"C:\not-a-repo";
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Not.Empty,
                "유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패합니다");
        }
```

- [ ] **Step 4: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: 컴파일 실패 — `ConnectRepositoryCommand` 정의 없음(CS1061), 생성자 인수 개수 불일치(CS1729)

- [ ] **Step 5: ViewModel에 명령을 구현**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`에서 다섯 곳을 고친다.

(a) 필드 선언부의 `private readonly IWorkingTreeCleaner _cleaner;` 앞에 추가:

```csharp
        private readonly IFolderBrowseDialog _folderDialog;
```

(b) 생성자 시그니처의 마지막에 매개변수를 추가하고 초기화한다:

```csharp
            IFileSaveDialog? saveDialog = null,
            IWorkingTreeCleaner? cleaner = null,
            IFolderBrowseDialog? folderDialog = null)
```

```csharp
            _cleaner = cleaner ?? new WorkingTreeCleaner();
            _folderDialog = folderDialog ?? new FolderBrowserDialogAdapter();
```

(c) 생성자의 명령 등록부, `ConnectCommand = ...` 다음 줄에 추가:

```csharp
            ConnectRepositoryCommand = new RelayCommand(ConnectRepository, CanConnectRepository);
```

(d) `ConnectCommand` 속성 선언 다음에 추가:

```csharp
        /// <summary>
        /// 활성 데이터베이스에 Git 저장소를 매핑한다.
        /// 매핑이 없으면 추출도 커밋도 불가능하므로 여기가 첫 설정 경로다.
        /// </summary>
        public ICommand ConnectRepositoryCommand { get; }
```

(e) `// ---------- Setup ----------` 주석 앞에 명령 본문을 추가:

```csharp
        // ---------- 저장소 매핑 ----------

        private bool CanConnectRepository() => HasContext && !IsMapped;

        private void ConnectRepository()
        {
            if (!CanConnectRepository()) return;

            var path = _folderDialog.PromptForFolder(
                $"'{ServerName}.{DatabaseName}'의 스크립트를 보관할 Git 저장소 폴더를 선택하세요.", null);

            // 사용자가 취소한 경우다. 오류가 아니다.
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!_gitManager.IsRepository(path!))
            {
                // 유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패한다.
                _notifier.ShowError("DBVC", $"'{path}'은(는) Git 저장소가 아닙니다. git init된 폴더를 선택하세요.");
                return;
            }

            _configManager.AddMapping(ServerName!, DatabaseName!, path!);

            // 매핑·초기화 상태를 다시 판정하고 목록을 새로고침한다.
            SetContext(ServerName, DatabaseName);
        }
```

- [ ] **Step 6: `CanExecute` 재평가에 새 명령을 포함**

같은 파일 하단의 `RaiseCommitCanExecuteChanged`를 이름과 내용 모두 바꾼다. 커밋 전용이 아니게 되었으므로 이름을 맞춘다.

```csharp
        private void RaiseActionCanExecuteChanged()
        {
            (CommitCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateDeploymentScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateRollbackScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConnectRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
```

먼저 호출처를 모두 찾는다.

```bash
grep -n "RaiseCommitCanExecuteChanged" src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs
```

`IsMapped` setter, `CommitMessage` setter, `Refresh()`의 시작과 끝, `RaiseConnectCanExecuteChanged()` — 총 5곳이 나온다.
전부 `RaiseActionCanExecuteChanged()`로 바꾼 뒤 같은 grep을 다시 돌려 결과가 비었는지 확인한다.

- [ ] **Step 7: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: PASS (신규 4개 포함)

- [ ] **Step 8: 전체 빌드와 테스트로 회귀 확인**

```bash
dotnet build DBVC.slnx && dotnet test DBVC.slnx -f net10.0
```

Expected: 빌드 성공, 테스트 전부 통과

- [ ] **Step 9: 커밋**

```bash
git add src/DBVC.Vsix/Services/IFolderBrowseDialog.cs src/DBVC.Vsix/DBVC.Vsix.csproj src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): DB에 Git 저장소를 매핑하는 ConnectRepositoryCommand

매핑 등록 UI가 없어 사용자가 mappings.json을 직접 만들어야 하던 문제를
해결한다. 선택한 폴더가 Git 저장소가 아니면 저장하지 않는다."
```

---

## Task 5: 경고 배너 승격과 저장소 연결 버튼 (XAML)

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`

**Interfaces:**
- Consumes: Task 4의 `ConnectRepositoryCommand`, 기존 `IsMapped`·`HasWarning`·`WarningMessage`·`IsInitialized`

**배경:** 현재 경고 배너는 `IsInitialized == true`인 Grid 안에 있어 초기화 전에는 보이지 않는다. 매핑 등록은 초기화 여부와 무관하게 가능해야 하므로 배너를 최상위로 올린다. Setup 오버레이가 중복 표시하던 `WarningMessage`는 제거한다.

이 태스크에는 자동화 테스트가 없다. WPF 레이아웃은 CI에서 검증할 수 없으며, README가 이미 "CI로 검증되지 않는 것"으로 분류한 범주다.

- [ ] **Step 1: XAML을 교체**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml` 전체를 다음으로 바꾼다.

```xml
<UserControl x:Class="DBVC.Vsix.UI.ViewChangesControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:avalonEdit="http://icsharpcode.net/sharpdevelop/avalonedit"
             xmlns:local="clr-namespace:DBVC.Vsix.UI">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
        <local:InverseBooleanToVisibilityConverter x:Key="InverseBoolToVis"/>
    </UserControl.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- 대상 데이터베이스 지정 -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="5,5,5,0">
            <TextBlock Text="Server:" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <TextBox Text="{Binding ServerName, UpdateSourceTrigger=PropertyChanged}" Width="140" Margin="0,0,10,0"/>
            <TextBlock Text="Database:" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <TextBox Text="{Binding DatabaseName, UpdateSourceTrigger=PropertyChanged}" Width="140" Margin="0,0,10,0"/>
            <Button Content="Connect" Command="{Binding ConnectCommand}" Width="80"/>
        </StackPanel>

        <!--
            경고 배너. 초기화 여부와 무관하게 보여야 매핑되지 않은 DB에서도
            저장소를 연결할 수 있다.
        -->
        <Border Grid.Row="1" Background="#FFF4CE" BorderBrush="#E0C77A" BorderThickness="1"
                Padding="8,5" Margin="5,5,5,0"
                Visibility="{Binding HasWarning, Converter={StaticResource BoolToVis}}">
            <DockPanel LastChildFill="True">
                <Button DockPanel.Dock="Right" Content="저장소 연결..." Width="110" Margin="8,0,0,0"
                        Command="{Binding ConnectRepositoryCommand}"
                        Visibility="{Binding IsMapped, Converter={StaticResource InverseBoolToVis}}"
                        ToolTip="이 데이터베이스의 스크립트를 보관할 Git 저장소 폴더를 지정합니다."/>
                <TextBlock Text="{Binding WarningMessage}" Foreground="#6B5A00"
                           TextWrapping="Wrap" FontWeight="SemiBold" VerticalAlignment="Center"/>
            </DockPanel>
        </Border>

        <Grid Grid.Row="2" Visibility="{Binding IsInitialized, Converter={StaticResource BoolToVis}}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="5" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <!-- Top Area -->
            <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="5">
                <Button Content="Refresh" Command="{Binding RefreshCommand}" Width="70" Margin="0,0,10,0"/>
                <TextBox Text="{Binding CommitMessage, UpdateSourceTrigger=PropertyChanged}"
                         Width="240" Margin="0,0,10,0"
                         IsEnabled="{Binding IsMapped}"/>
                <Button Content="Commit" Command="{Binding CommitCommand}" Width="70" Margin="0,0,16,0" />
                <Button Content="Deployment Script" Command="{Binding GenerateDeploymentScriptCommand}" Width="130" Margin="0,0,6,0"
                        ToolTip="선택한 객체의 현재 DDL을 단일 .sql 파일로 병합합니다." />
                <Button Content="Rollback Script" Command="{Binding GenerateRollbackScriptCommand}" Width="120"
                        ToolTip="선택한 객체가 마지막으로 커밋되기 직전 코드를 단일 .sql 파일로 병합합니다." />
            </StackPanel>

            <!-- Middle Area -->
            <ListView Grid.Row="1" ItemsSource="{Binding Changes}" SelectedItem="{Binding SelectedChange}">
                <ListView.View>
                    <GridView>
                        <GridViewColumn Header="Stage">
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <CheckBox IsChecked="{Binding IsSelected}"/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Header="State" DisplayMemberBinding="{Binding State}"/>
                        <GridViewColumn Header="Object" DisplayMemberBinding="{Binding ObjectName}"/>
                    </GridView>
                </ListView.View>
            </ListView>

            <GridSplitter Grid.Row="2" Height="5" HorizontalAlignment="Stretch" />

            <!-- Bottom Area -->
            <Grid Grid.Row="3">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="5" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <avalonEdit:TextEditor x:Name="OldTextEditor" Grid.Column="0" IsReadOnly="True" SyntaxHighlighting="TSQL" />
                <GridSplitter Grid.Column="1" Width="5" HorizontalAlignment="Stretch" />
                <avalonEdit:TextEditor x:Name="NewTextEditor" Grid.Column="2" IsReadOnly="True" SyntaxHighlighting="TSQL" />
            </Grid>
        </Grid>

        <!-- The Setup Overlay. 경고는 위 배너가 담당하므로 여기서 중복 표시하지 않는다. -->
        <Grid Grid.Row="2" Visibility="{Binding IsInitialized, Converter={StaticResource InverseBoolToVis}}" Background="#F0F0F0">
            <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
                <TextBlock Text="This database is not initialized for DBVC." FontSize="16" Margin="0,0,0,20" Foreground="#333333"/>
                <Button Content="Setup DBVC" Command="{Binding SetupCommand}" Width="150" Height="40" FontSize="14" Cursor="Hand"/>
            </StackPanel>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Step 2: 빌드가 통과하는지 확인**

```bash
dotnet build DBVC.slnx
```

Expected: 빌드 성공. `x:Name` 두 개(`OldTextEditor`, `NewTextEditor`)가 유지되어야 코드비하인드가 컴파일된다.

- [ ] **Step 3: 전체 테스트로 회귀 확인**

```bash
dotnet test DBVC.slnx -f net10.0
```

Expected: 전부 통과

- [ ] **Step 4: 커밋**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml
git commit -m "feat(vsix): 경고 배너를 최상위로 올리고 저장소 연결 버튼 배치

배너가 초기화 여부와 무관하게 보이므로 매핑되지 않은 DB에서도
저장소를 연결할 수 있다. Setup 오버레이의 중복 경고 표시는 제거했다."
```

---

## Task 6: `DiffTextBuilder` (순수 변환)

**Files:**
- Create: `src/DBVC.Vsix/Services/DiffTextBuilder.cs`
- Test: `tests/DBVC.Vsix.Tests/Services/DiffTextBuilderTests.cs` (신규)

**Interfaces:**
- Consumes: DiffPlex `DiffPiece`(`Text`, `Type`), `ChangeType`(`Unchanged`/`Inserted`/`Deleted`/`Modified`/`Imaginary`) — 둘 다 `DiffPlex.DiffBuilder.Model` 네임스페이스
- Produces:
  - `enum DiffLineKind { Unchanged, Inserted, Deleted, Modified, Padding }`
  - `class DiffPane { string Text; IReadOnlyList<DiffLineKind> LineKinds; }`
  - `static DiffPane DiffTextBuilder.Build(IEnumerable<DiffPiece>? lines)`

- [ ] **Step 1: 실패하는 테스트를 작성**

`tests/DBVC.Vsix.Tests/Services/DiffTextBuilderTests.cs`를 새로 만든다.

```csharp
using System.Collections.Generic;
using System.Linq;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using NUnit.Framework;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class DiffTextBuilderTests
    {
        // DiffPlex의 Imaginary 줄은 Text가 null이다. 생성자 매개변수는 non-nullable로 선언되어 있어 `!`가 필요하다.
        private static DiffPiece Line(string? text, ChangeType type) => new DiffPiece(text!, type);

        [Test]
        public void Build_KeepsLineOrderAndJoinsWithNewlines()
        {
            var pane = DiffTextBuilder.Build(new[]
            {
                Line("CREATE TABLE Users (", ChangeType.Unchanged),
                Line("  Id INT", ChangeType.Unchanged),
                Line(");", ChangeType.Unchanged)
            });

            Assert.That(pane.Text, Is.EqualTo("CREATE TABLE Users (\n  Id INT\n);"));
        }

        [Test]
        public void Build_TurnsImaginaryLinesIntoEmptyPaddingLines()
        {
            var pane = DiffTextBuilder.Build(new[]
            {
                Line("A", ChangeType.Unchanged),
                Line(null, ChangeType.Imaginary),
                Line("B", ChangeType.Unchanged)
            });

            Assert.That(pane.Text, Is.EqualTo("A\n\nB"), "패딩 줄은 좌우 정렬을 위한 빈 줄입니다");
            Assert.That(pane.LineKinds[1], Is.EqualTo(DiffLineKind.Padding));
        }

        [TestCase(ChangeType.Unchanged, DiffLineKind.Unchanged)]
        [TestCase(ChangeType.Inserted, DiffLineKind.Inserted)]
        [TestCase(ChangeType.Deleted, DiffLineKind.Deleted)]
        [TestCase(ChangeType.Modified, DiffLineKind.Modified)]
        [TestCase(ChangeType.Imaginary, DiffLineKind.Padding)]
        public void Build_MapsEveryDiffPlexChangeType(ChangeType type, DiffLineKind expected)
        {
            var pane = DiffTextBuilder.Build(new[] { Line("x", type) });

            Assert.That(pane.LineKinds.Single(), Is.EqualTo(expected));
        }

        [Test]
        public void Build_ProducesOneLineKindPerTextLine()
        {
            var model = SideBySideDiffBuilder.Diff("A\nB\nC", "A\nX\nC\nD");

            var oldPane = DiffTextBuilder.Build(model.OldText.Lines);
            var newPane = DiffTextBuilder.Build(model.NewText.Lines);

            Assert.That(oldPane.LineKinds.Count, Is.EqualTo(oldPane.Text.Split('\n').Length),
                "렌더러가 줄 번호로 종류를 찾으므로 개수가 어긋나면 안 됩니다");
            Assert.That(newPane.LineKinds.Count, Is.EqualTo(newPane.Text.Split('\n').Length));
            Assert.That(oldPane.LineKinds.Count, Is.EqualTo(newPane.LineKinds.Count),
                "좌우 줄 수가 같아야 스크롤 동기화가 의미를 가집니다");
        }

        [Test]
        public void Build_ReturnsAnEmptyPane_ForNullOrEmptyInput()
        {
            Assert.That(DiffTextBuilder.Build(null).Text, Is.Empty);
            Assert.That(DiffTextBuilder.Build(null).LineKinds, Is.Empty);
            Assert.That(DiffTextBuilder.Build(new List<DiffPiece>()).Text, Is.Empty);
            Assert.That(DiffTextBuilder.Build(new List<DiffPiece>()).LineKinds, Is.Empty);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~DiffTextBuilderTests"
```

Expected: 컴파일 실패 — `DiffTextBuilder`, `DiffPane`, `DiffLineKind` 형식을 찾을 수 없음(CS0246)

- [ ] **Step 3: `DiffTextBuilder`를 구현**

`src/DBVC.Vsix/Services/DiffTextBuilder.cs`를 새로 만든다.

```csharp
using System.Collections.Generic;
using System.Text;
using DiffPlex.DiffBuilder.Model;

namespace DBVC.Vsix.Services
{
    /// <summary>Diff 한 줄의 종류. 배경색 결정에 쓴다.</summary>
    public enum DiffLineKind
    {
        Unchanged,
        Inserted,
        Deleted,
        Modified,

        /// <summary>반대편에만 줄이 있어 좌우를 맞추려고 넣은 빈 줄.</summary>
        Padding
    }

    /// <summary>에디터 한쪽에 넣을 텍스트와 줄별 종류.</summary>
    public class DiffPane
    {
        public string Text { get; set; } = string.Empty;

        /// <summary>1-based 줄 번호에 대응한다. 인덱스 0이 문서의 1번 줄이다.</summary>
        public IReadOnlyList<DiffLineKind> LineKinds { get; set; } = new List<DiffLineKind>();
    }

    /// <summary>
    /// DiffPlex의 한쪽 결과를 AvalonEdit에 넣을 텍스트와 줄 종류로 바꾼다.
    /// WPF·파일 시스템에 의존하지 않는 순수 변환이다.
    /// </summary>
    public static class DiffTextBuilder
    {
        public static DiffPane Build(IEnumerable<DiffPiece>? lines)
        {
            var kinds = new List<DiffLineKind>();
            var builder = new StringBuilder();
            var isFirst = true;

            foreach (var line in lines ?? new List<DiffPiece>())
            {
                if (line == null) continue;

                if (!isFirst) builder.Append('\n');
                isFirst = false;

                // Imaginary 줄은 Text가 null이다. 빈 줄로 만들어 좌우 정렬을 맞춘다.
                builder.Append(line.Text ?? string.Empty);
                kinds.Add(MapChangeType(line.Type));
            }

            return new DiffPane { Text = builder.ToString(), LineKinds = kinds };
        }

        private static DiffLineKind MapChangeType(ChangeType type)
        {
            switch (type)
            {
                case ChangeType.Inserted: return DiffLineKind.Inserted;
                case ChangeType.Deleted: return DiffLineKind.Deleted;
                case ChangeType.Modified: return DiffLineKind.Modified;
                case ChangeType.Imaginary: return DiffLineKind.Padding;
                default: return DiffLineKind.Unchanged;
            }
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~DiffTextBuilderTests"
```

Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/Services/DiffTextBuilder.cs tests/DBVC.Vsix.Tests/Services/DiffTextBuilderTests.cs
git commit -m "feat(vsix): DiffPlex 결과를 에디터 텍스트와 줄 종류로 변환

Imaginary 줄을 빈 줄로 만들어 좌우를 정렬한다.
순수 변환이라 WPF 없이 전량 단위 테스트한다."
```

---

## Task 7: Diff 배경 렌더러와 컨트롤 연결

**Files:**
- Create: `src/DBVC.Vsix/UI/DiffLineBackgroundRenderer.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs`

**Interfaces:**
- Consumes: Task 6의 `DiffTextBuilder.Build`, `DiffPane`, `DiffLineKind`. 기존 `DiffService.GetDiffModel(string, string, string?)` → `SideBySideDiffModel`(`OldText.Lines`, `NewText.Lines`)
- Produces: `DiffLineBackgroundRenderer` — 생성자 `(TextView textView)`, `SetLineKinds(IReadOnlyList<DiffLineKind>?)`

**배경:** `DiffService.GetDiffModel`은 지금까지 테스트에서만 호출되었다. 컨트롤은 양쪽 원문을 평문으로 넣기만 해서, 설계가 명시한 diff 강조가 화면에 나타나지 않는다.

이 태스크의 렌더링·스크롤 동기화는 WPF 런타임이 필요해 자동화 테스트 대상이 아니다. 빌드 통과와 기존 테스트 회귀 없음까지 확인한 뒤, SSMS 21에서 수동 검증한다.

- [ ] **Step 1: 배경 렌더러를 작성**

`src/DBVC.Vsix/UI/DiffLineBackgroundRenderer.cs`를 새로 만든다.

```csharp
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DBVC.Vsix.Services;
using ICSharpCode.AvalonEdit.Rendering;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// Diff 줄의 배경을 칠한다. 화면에 보이는 줄만 그린다.
    /// </summary>
    public class DiffLineBackgroundRenderer : IBackgroundRenderer
    {
        private readonly TextView _textView;
        private IReadOnlyList<DiffLineKind> _lineKinds = new List<DiffLineKind>();

        public DiffLineBackgroundRenderer(TextView textView)
        {
            _textView = textView;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public Brush InsertedBrush { get; set; } = Frozen("#E6FFED");
        public Brush DeletedBrush { get; set; } = Frozen("#FFEEF0");
        public Brush ModifiedBrush { get; set; } = Frozen("#FFF5B1");
        public Brush PaddingBrush { get; set; } = Frozen("#F0F0F0");

        /// <summary>
        /// 줄 종류를 교체하고 배경을 다시 그리게 한다.
        /// 텍스트를 먼저 설정한 뒤 호출해야 이전 종류로 한 번 그리는 일이 없다.
        /// </summary>
        public void SetLineKinds(IReadOnlyList<DiffLineKind>? lineKinds)
        {
            _lineKinds = lineKinds ?? new List<DiffLineKind>();
            _textView.InvalidateLayer(Layer);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_lineKinds.Count == 0) return;

            textView.EnsureVisualLines();

            foreach (var visualLine in textView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;

                // 텍스트와 종류 배열이 잠시 어긋난 순간에도 예외를 던지지 않는다.
                if (lineNumber < 1 || lineNumber > _lineKinds.Count) continue;

                var brush = BrushFor(_lineKinds[lineNumber - 1]);
                if (brush == null) continue;

                // 빈 줄도 칠해야 패딩이 보이므로 사각형 폭은 뷰 전체로 잡는다.
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, visualLine.FirstDocumentLine))
                {
                    drawingContext.DrawRectangle(brush, null,
                        new Rect(0, rect.Top, textView.ActualWidth, rect.Height));
                }
            }
        }

        private Brush? BrushFor(DiffLineKind kind)
        {
            switch (kind)
            {
                case DiffLineKind.Inserted: return InsertedBrush;
                case DiffLineKind.Deleted: return DeletedBrush;
                case DiffLineKind.Modified: return ModifiedBrush;
                case DiffLineKind.Padding: return PaddingBrush;
                default: return null;
            }
        }

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

            // 고정 색이므로 얼려 두면 렌더링마다 재검증하지 않는다.
            brush.Freeze();
            return brush;
        }
    }
}
```

- [ ] **Step 2: 컨트롤을 Diff 모델에 연결하고 스크롤을 동기화**

`src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` 전체를 다음으로 바꾼다.

```csharp
using System;
using System.Windows.Controls;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;
using ICSharpCode.AvalonEdit;

namespace DBVC.Vsix.UI
{
    public partial class ViewChangesControl : UserControl
    {
        private readonly ViewChangesViewModel _viewModel;
        private readonly DiffService _diffService;
        private readonly DiffLineBackgroundRenderer _oldRenderer;
        private readonly DiffLineBackgroundRenderer _newRenderer;
        private bool _syncingScroll;

        public ViewChangesControl()
            : this(DbvcServices.Default.SharedViewChangesViewModel, DbvcServices.Default.CreateDiffService())
        {
        }

        public ViewChangesControl(ViewChangesViewModel viewModel, DiffService? diffService)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _diffService = diffService ?? new DiffService();

            InitializeComponent();
            DataContext = _viewModel;

            _oldRenderer = new DiffLineBackgroundRenderer(OldTextEditor.TextArea.TextView);
            _newRenderer = new DiffLineBackgroundRenderer(NewTextEditor.TextArea.TextView);
            OldTextEditor.TextArea.TextView.BackgroundRenderers.Add(_oldRenderer);
            NewTextEditor.TextArea.TextView.BackgroundRenderers.Add(_newRenderer);

            OldTextEditor.TextArea.TextView.ScrollOffsetChanged += OnOldScrollOffsetChanged;
            NewTextEditor.TextArea.TextView.ScrollOffsetChanged += OnNewScrollOffsetChanged;

            _viewModel.SelectionChanged += OnSelectionChanged;
            Unloaded += (_, __) =>
            {
                _viewModel.SelectionChanged -= OnSelectionChanged;
                OldTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnOldScrollOffsetChanged;
                NewTextEditor.TextArea.TextView.ScrollOffsetChanged -= OnNewScrollOffsetChanged;
            };
        }

        /// <summary>
        /// 선택된 객체의 Git HEAD 버전과 현재 DB 버전을 좌우 에디터에 채우고 차이를 강조한다.
        /// </summary>
        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            var selected = _viewModel.SelectedChange;
            if (selected == null || _viewModel.ServerName == null || _viewModel.DatabaseName == null)
            {
                SetPane(OldTextEditor, _oldRenderer, new DiffPane());
                SetPane(NewTextEditor, _newRenderer, new DiffPane());
                return;
            }

            // Diff 생성 실패(신규 객체 등)는 빈 쪽으로 자연스럽게 표현되며 예외를 던지지 않는다.
            var model = _diffService.GetDiffModel(
                _viewModel.ServerName,
                _viewModel.DatabaseName,
                selected.RelativePath);

            SetPane(OldTextEditor, _oldRenderer, DiffTextBuilder.Build(model.OldText.Lines));
            SetPane(NewTextEditor, _newRenderer, DiffTextBuilder.Build(model.NewText.Lines));
        }

        /// <summary>텍스트를 먼저 넣고 줄 종류를 넘긴다. 순서가 반대면 이전 종류로 한 번 그린다.</summary>
        private static void SetPane(TextEditor editor, DiffLineBackgroundRenderer renderer, DiffPane pane)
        {
            editor.Text = pane.Text;
            renderer.SetLineKinds(pane.LineKinds);
        }

        private void OnOldScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(OldTextEditor, NewTextEditor);

        private void OnNewScrollOffsetChanged(object? sender, EventArgs e) => SyncScroll(NewTextEditor, OldTextEditor);

        /// <summary>좌우가 줄 단위로 정렬되어 있으므로 오프셋을 그대로 옮긴다.</summary>
        private void SyncScroll(TextEditor source, TextEditor target)
        {
            if (_syncingScroll) return;

            _syncingScroll = true;
            try
            {
                target.ScrollToVerticalOffset(source.VerticalOffset);
                target.ScrollToHorizontalOffset(source.HorizontalOffset);
            }
            finally
            {
                _syncingScroll = false;
            }
        }
    }
}
```

- [ ] **Step 3: 빌드가 통과하는지 확인**

```bash
dotnet build DBVC.slnx
```

Expected: 빌드 성공

- [ ] **Step 4: 전체 테스트로 회귀 확인**

```bash
dotnet test DBVC.slnx -f net10.0
```

Expected: 전부 통과. `DiffServiceTests`가 여전히 통과해야 한다 — `GetDiffModel`의 계약은 바뀌지 않았고 호출자만 늘었다.

- [ ] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/UI/DiffLineBackgroundRenderer.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs
git commit -m "feat(vsix): Diff 줄 배경 강조와 좌우 스크롤 동기화

지금까지 테스트에서만 쓰이던 DiffService.GetDiffModel을 UI에 연결한다.
추가/삭제/수정/패딩 줄을 색으로 구분하고 양쪽 스크롤을 맞춘다."
```

---

## Task 8: 문서 갱신

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-07-31-dbvc-view-changes-design.md`
- Modify: `docs/superpowers/specs/2026-07-31-dbvc-core-engine-design.md`

**Interfaces:**
- Consumes: Task 1~7의 최종 동작

- [ ] **Step 1: README의 Diff 설명을 실제 동작에 맞춤**

`README.md` 11행의 `AvalonEdit`/`DiffPlex` 항목을 다음으로 바꾼다.

```markdown
  - `AvalonEdit` 및 `DiffPlex`를 활용하여 변경 전(Old)과 변경 후(New)의 SQL 코드를 T-SQL 문법 하이라이팅이 적용된 좌우 분할(Side-by-Side) 뷰로 비교할 수 있습니다. 추가·삭제·수정된 줄은 배경색으로 구분되며, 좌우 줄이 정렬되고 스크롤이 함께 움직입니다.
```

- [ ] **Step 2: README 사용법에 매핑 등록 단계를 추가**

`README.md`의 "2. **대상 데이터베이스 지정:**" 항목 전체를 다음 두 항목으로 바꾼다. 이후 항목 번호(3~5)를 4~6으로 하나씩 올린다.

```markdown
2. **대상 데이터베이스 지정:** 패널 상단의 **Server / Database** 입력란에 대상을 입력하고 **"Connect"** 를 누릅니다.
3. **Git 저장소 연결:** 해당 데이터베이스가 Git 저장소에 매핑되어 있지 않으면 경고 배너가 나타나고 커밋이 비활성화됩니다. 배너의 **"저장소 연결..."** 버튼을 눌러 스크립트를 보관할 폴더를 지정하세요. 이미 `git init`된 폴더여야 하며, 아니면 오류가 표시되고 매핑되지 않습니다. 매핑은 `%APPDATA%\DBVC\mappings.json`에 저장됩니다.
```

- [ ] **Step 3: README에 삭제 객체 처리를 명시**

`README.md`의 "5. **Git 커밋:**"(Step 2에서 번호가 6이 된 항목) 뒤에 문장을 덧붙인다.

```markdown
   데이터베이스에서 삭제(DROP)된 객체는 새로고침 시 해당 `.sql` 파일이 저장소에서 함께 제거되므로, 커밋하면 삭제가 그대로 형상 관리에 반영됩니다.
```

- [ ] **Step 4: view-changes 설계에 매핑 등록 흐름과 배너 위치를 반영**

`docs/superpowers/specs/2026-07-31-dbvc-view-changes-design.md`의 `## Error Handling` 절 마지막 항목(`If ConfigManager cannot resolve a mapping...`)을 다음으로 바꾼다.

```markdown
- If `ConfigManager` cannot resolve a mapping for the active database, a warning banner is shown above the content area ("Active Database is not mapped to a Git repository.") and commit actions are disabled. The banner also carries a **"저장소 연결..."** button that prompts for a folder, verifies it is a valid Git repository via `IGitManager.IsRepository`, and registers the mapping through `ConfigManager.AddMapping`. The banner sits outside the initialization overlay so that an uninitialized database can still be mapped.
```

- [ ] **Step 5: core-engine 설계에 작업 트리 정리 규칙을 추가**

`docs/superpowers/specs/2026-07-31-dbvc-core-engine-design.md`의 `### 3.1. SmoManager (객체 스크립팅)` 절 바로 뒤, `### 3.2. GitManager (Git 제어)` 앞에 절을 삽입한다.

```markdown
#### 3.1.1. 삭제된 객체의 작업 트리 정리

`SmoManager`는 존재하는 객체만 추출하므로 DROP된 객체의 `.sql` 파일은 아무도 지우지 않는다.
파일이 남으면 Git이 삭제를 감지하지 못해 커밋되지 않으므로 `WorkingTreeCleaner`가 이를 정리한다.

삭제 대상은 아래를 **모두** 만족하는 항목뿐이다.

| 조건 | 이유 |
| --- | --- |
| `State`가 `Deleted` | 삭제된 객체만 대상이다 |
| `LastLogId > 0` | DDL 로그에 근거가 있는 항목만 지운다. Git 상태에서만 유래한 항목은 이미 파일이 없다 |
| `[Schema]/[ObjectType]/[Name].sql` 규약에 맞는 경로 | 규약 밖의 파일은 DBVC가 만든 것이 아니다 |
| 저장소 루트 하위 경로 | `..`가 섞인 경로도 규약 검사는 통과하므로 마지막 방어선이 필요하다 |

호출은 `RefreshState` 직후다. Git 상태를 읽은 뒤이므로 정리가 상태 판정을 바꾸지 않는다.
개별 파일의 삭제 실패는 격리해 나머지를 계속 처리하고, 실패 목록은 사용자에게 알린다.
트리거 설치 이전에 삭제된 객체는 로그에 근거가 없어 자동 정리 대상이 아니다.
```

- [ ] **Step 6: 문서가 실제 동작과 맞는지 확인**

```bash
dotnet test DBVC.slnx -f net10.0
```

Expected: 전부 통과. 그리고 README·설계 문서에 남은 진술 중 코드와 어긋나는 것이 없는지 눈으로 확인한다.

- [ ] **Step 7: 커밋**

```bash
git add README.md docs/superpowers/specs/2026-07-31-dbvc-view-changes-design.md docs/superpowers/specs/2026-07-31-dbvc-core-engine-design.md
git commit -m "docs: P1 결함 수정 결과를 README와 설계 문서에 반영

Diff 강조 동작, 저장소 매핑 등록 절차, 삭제된 객체의 작업 트리 정리 규칙."
```

---

## 완료 후 수동 검증 (SSMS 21 필요)

CI가 검증하지 못하는 항목이다. Windows + SSMS 21 환경에서 확인한다.

- [ ] 매핑되지 않은 DB를 Connect → 경고 배너와 "저장소 연결..." 버튼이 보인다
- [ ] 버튼 → 폴더 선택 → git init된 폴더를 고르면 매핑되고 목록이 채워진다
- [ ] git init되지 않은 폴더를 고르면 오류가 뜨고 매핑되지 않는다
- [ ] 초기화되지 않은 DB에서도 배너와 버튼이 보인다 (Setup 오버레이가 가리지 않는다)
- [ ] 객체를 수정하고 Refresh → 항목 선택 시 변경 줄이 색으로 구분되고 좌우가 정렬된다
- [ ] 한쪽을 스크롤하면 반대쪽이 함께 움직인다
- [ ] `DROP TABLE`한 뒤 Refresh → 목록에 `Deleted`로 뜨고 저장소의 `.sql` 파일이 사라진다
- [ ] 그 항목을 체크하고 Commit → 삭제가 커밋되고 목록에서 사라진다
