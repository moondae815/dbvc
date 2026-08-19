# DBVC Pull·Object History UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 코어에만 존재하던 Feature 6(Git Pull)과 Feature 7(Object History)을 View Changes 도구 창에 연결한다.

**Architecture:** Pull은 `ViewChangesViewModel`에 명령 하나를 더하되, 충돌 시 미커밋 변경이 사라지는 것을 사전에 알리기 위해 `IUserNotifier`에 확인·정보 알림을 추가한다. 이력은 별도 `ObjectHistoryViewModel`에 두어 이미 529줄인 `ViewChangesViewModel`이 더 자라지 않게 하고, 선택된 객체가 바뀔 때 갱신한다. 화면은 하단을 `[Diff] [History]` 탭으로 나누고 액션 영역을 `WrapPanel`로 바꾼다.

**Tech Stack:** C# / .NET Framework 4.8, WPF, AvalonEdit 6.3, LibGit2Sharp 0.32, NUnit 4, Moq

## Global Constraints

- `DBVC.Core`는 `net48;netstandard2.0` 멀티타깃, `DBVC.Vsix`는 `net48` 단일 타깃이다. **이 작업은 Core를 전혀 건드리지 않는다** — `PullChanges`와 `GetHistory`는 이미 구현·테스트되어 있다.
- 모든 프로젝트가 `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`이다.
- 코드 주석과 커밋 메시지 본문은 한국어, 테스트 메서드 이름은 영어 서술형(`Method_DoesSomething_WhenCondition`)이다. 기존 파일의 관례를 그대로 따른다.
- 테스트는 NUnit 4 + Moq다.
- **macOS·Linux에서는 `net10.0` 타깃만 실행할 수 있다.** 이 계획의 테스트 명령은 모두 `-f net10.0`을 붙인다.
- 커밋 메시지 제목 형식: `feat(vsix):`, `fix(vsix):`, `docs:`.
- **Pull 성공 후 `Refresh()`를 호출하지 않는다.** Refresh는 SMO로 현재 DB를 다시 추출해 작업 트리를 덮어쓰므로, 방금 받은 원격 변경이 즉시 사라진다. 이 금지는 테스트로 고정한다.

---

## File Structure

| 파일 | 책임 | 태스크 |
| --- | --- | --- |
| `src/DBVC.Vsix/Services/IUserNotifier.cs` (수정) | `ShowInfo`·`Confirm` 추가 + `MessageBoxNotifier` 구현 | 1 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (수정) | `PullCommand` | 1 |
| `src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs` (신규) | 이력 조회·변환 + `HistoryEntryViewModel` | 2 |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (수정) | `History` 속성, 선택 변경 시 갱신, 목록을 비울 때 선택 해제 | 3 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (수정) | `WrapPanel` 액션 영역, 하단 `TabControl` | 4 |
| `README.md` (수정) | Pull·History 사용법 | 5 |

---

## Task 1: Pull 명령과 `IUserNotifier` 확장

**Files:**
- Modify: `src/DBVC.Vsix/Services/IUserNotifier.cs`
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: `IGitManager.PullChanges(string, string) → bool`, `IGitManager.GetChangedFiles(string) → IReadOnlyList<string>`, `IConfigManager.TryGetMapping(string, string) → MappingConfig?`, `DBVC.Core.MergeConflictException`
- Produces:
  - `IUserNotifier.ShowInfo(string title, string message)` 및 `IUserNotifier.Confirm(string title, string message) → bool`
  - `ViewChangesViewModel.PullCommand` (`ICommand`)
  - 테스트 더블 `RecordingNotifier`에 `Infos`, `ConfirmResult`(기본 `true`), `ConfirmCallCount` 추가

**배경:** `GitManager.PullChanges`는 충돌을 감지하면 `Reset(ResetMode.Hard)`로 병합을 되돌린다. 이때 추적 중인 파일의 미커밋 변경도 함께 사라진다. DBVC에서는 Refresh가 SMO로 모든 객체를 덮어쓰므로 이 상태가 오히려 일반적이라, 사전 고지 없이 사라지면 사용자는 원인을 알 수 없다.

- [x] **Step 1: `IUserNotifier`를 확장**

`src/DBVC.Vsix/Services/IUserNotifier.cs` 전체를 다음으로 바꾼다.

```csharp
using System.Windows;

namespace DBVC.Vsix.Services
{
    /// <summary>
    /// 사용자에게 결과를 알리고 진행 여부를 묻는다. ViewModel이 WPF에 직접 의존하지 않도록 분리한다.
    /// </summary>
    public interface IUserNotifier
    {
        void ShowError(string title, string message);

        /// <summary>완료·요약처럼 오류가 아닌 결과를 알린다.</summary>
        void ShowInfo(string title, string message);

        /// <summary>진행 여부를 묻는다. 사용자가 계속을 선택하면 <c>true</c>.</summary>
        bool Confirm(string title, string message);
    }

    /// <summary>
    /// 설계에 명시된 대로 WPF <c>MessageBox</c>로 표시한다.
    /// </summary>
    public class MessageBoxNotifier : IUserNotifier
    {
        public void ShowError(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public void ShowInfo(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public bool Confirm(string title, string message)
        {
            // 되돌릴 수 없는 손실을 경고하는 자리이므로 Warning 아이콘을 쓴다.
            return MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                == MessageBoxResult.OK;
        }
    }
}
```

- [x] **Step 2: 테스트 더블을 확장하고 기본 스텁을 추가**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` 파일 하단의 `RecordingNotifier` 클래스를 다음으로 바꾼다.

```csharp
        private sealed class RecordingNotifier : IUserNotifier
        {
            public List<string> Errors { get; } = new List<string>();
            public List<string> Infos { get; } = new List<string>();

            /// <summary>Confirm의 응답. 기본이 "계속"이라 기존 테스트의 동작이 바뀌지 않는다.</summary>
            public bool ConfirmResult { get; set; } = true;
            public int ConfirmCallCount { get; private set; }

            public void ShowError(string title, string message) => Errors.Add(message);

            public void ShowInfo(string title, string message) => Infos.Add(message);

            public bool Confirm(string title, string message)
            {
                ConfirmCallCount++;
                return ConfirmResult;
            }
        }
```

같은 파일의 `SetUp` 메서드에서 `_smo.Setup(...)` 다음 줄에 추가한다. **이 스텁이 없으면 `Pull`이 `GetChangedFiles`의 `null` 반환에서 `NullReferenceException`을 던진다.**

```csharp
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>())).Returns(new List<string>());
```

- [x] **Step 3: 실패하는 테스트를 작성**

같은 파일에서 `// ---------- Commit ----------` 주석 바로 앞에 추가한다.

```csharp
        // ---------- Pull ----------

        [Test]
        public void PullCommand_IsEnabled_WhenTheDatabaseIsMapped()
        {
            Assert.That(NewConnectedViewModel().PullCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void PullCommand_IsDisabled_WhenTheDatabaseIsNotMapped()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);

            Assert.That(NewConnectedViewModel().PullCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void PullCommand_PullsWithoutAsking_WhenTheWorkingTreeIsClean()
        {
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.Zero, "잃을 것이 없으면 묻지 않습니다");
            _git.Verify(g => g.PullChanges(Server, Database), Times.Once);
        }

        [Test]
        public void PullCommand_AsksForConfirmation_WhenUncommittedChangesExist()
        {
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>()))
                .Returns(new List<string> { "dbo/Tables/Users.sql", "dbo/Views/vw_Sales.sql" });
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.EqualTo(1),
                "충돌 시 hard reset으로 미커밋 변경이 사라지므로 먼저 알려야 합니다");
            _git.Verify(g => g.PullChanges(Server, Database), Times.Once);
        }

        [Test]
        public void PullCommand_DoesNotPull_WhenTheUserCancelsTheConfirmation()
        {
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>()))
                .Returns(new List<string> { "dbo/Tables/Users.sql" });
            _notifier.ConfirmResult = false;
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            _git.Verify(g => g.PullChanges(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Empty, "취소는 오류가 아닙니다");
        }

        [Test]
        public void PullCommand_ReportsAMergeConflict()
        {
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new MergeConflictException("병합 충돌이 발생하여 Pull을 중단했습니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Does.Contain("충돌"));
        }

        [Test]
        public void PullCommand_ReportsAnUnexpectedFailure()
        {
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new InvalidOperationException("원격(remote)이 설정되어 있지 않습니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Does.Contain("원격"));
        }

        [Test]
        public void PullCommand_NotifiesOnSuccess()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(true);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Infos, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors, Is.Empty);
        }

        [Test]
        public void PullCommand_DoesNotRefresh_AfterASuccessfulPull()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(true);
            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();

            vm.PullCommand.Execute(null);

            _smo.Verify(
                s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>?>()),
                Times.Never,
                "Pull 직후 Refresh하면 방금 받은 원격 변경이 SMO 추출로 즉시 덮어써집니다");
        }
```

- [x] **Step 4: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: 컴파일 실패 — `PullCommand` 정의 없음(CS1061)

- [x] **Step 5: `PullCommand`를 구현**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`에서 네 곳을 고친다.

(a) 생성자의 명령 등록부, `ConnectRepositoryCommand = ...` 다음 줄에 추가:

```csharp
            PullCommand = new RelayCommand(Pull, CanPull);
```

(b) `ConnectRepositoryCommand` 속성 선언 다음에 추가:

```csharp
        /// <summary>원격 저장소의 변경을 로컬 저장소로 가져온다. (Feature 6)</summary>
        public ICommand PullCommand { get; }
```

(c) `// ---------- 저장소 매핑 ----------` 주석 앞에 명령 본문을 추가:

```csharp
        // ---------- Pull ----------

        private bool CanPull() => HasContext && IsMapped;

        private void Pull()
        {
            if (!CanPull()) return;

            var mapping = _configManager.TryGetMapping(ServerName!, DatabaseName!);
            if (mapping == null) return;

            // 충돌이 나면 GitManager가 hard reset으로 병합을 되돌리는데,
            // 그때 추적 중인 파일의 미커밋 변경도 함께 사라진다. 먼저 알린다.
            var pending = _gitManager.GetChangedFiles(mapping.GitPath);
            if (pending.Count > 0)
            {
                var proceed = _notifier.Confirm(
                    "DBVC Pull",
                    $"커밋하지 않은 변경 {pending.Count}개가 있습니다." + Environment.NewLine +
                    "Pull 중 충돌이 발생하면 병합을 되돌리면서 이 변경도 함께 사라집니다." + Environment.NewLine +
                    "(Refresh로 데이터베이스에서 다시 추출할 수 있습니다)" + Environment.NewLine + Environment.NewLine +
                    "계속하시겠습니까?");

                // 취소는 오류가 아니다.
                if (!proceed) return;
            }

            try
            {
                if (!_gitManager.PullChanges(ServerName!, DatabaseName!))
                {
                    _notifier.ShowError("DBVC Pull 실패", "매핑된 Git 저장소를 찾을 수 없습니다.");
                    return;
                }
            }
            catch (MergeConflictException ex)
            {
                // GitManager가 이미 병합을 되돌렸고 안내 문구도 담고 있다.
                _notifier.ShowError("DBVC Pull 중단", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _notifier.ShowError("DBVC Pull 실패", ex.Message);
                return;
            }

            // 여기서 Refresh를 부르면 안 된다. SMO 추출이 방금 받은 원격 변경을 즉시 덮어쓴다.
            _notifier.ShowInfo(
                "DBVC Pull",
                "원격 저장소의 변경을 가져왔습니다." + Environment.NewLine +
                "받은 스크립트를 확인한 뒤 필요하면 데이터베이스에 적용하세요.");
        }
```

(d) `RaiseActionCanExecuteChanged`에 한 줄 추가한다.

```csharp
            (PullCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

- [x] **Step 6: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: PASS (신규 9개 포함, 기존 테스트 회귀 없음)

- [x] **Step 7: 전체 테스트로 회귀 확인**

```bash
dotnet build DBVC.slnx && dotnet test DBVC.slnx -f net10.0
```

Expected: 빌드 성공, Core 179 + Vsix 86 통과

- [x] **Step 8: 커밋**

```bash
git add src/DBVC.Vsix/Services/IUserNotifier.cs src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 원격 변경을 가져오는 PullCommand (Feature 6)

충돌 시 hard reset으로 미커밋 변경이 사라지므로 먼저 확인을 받는다.
성공 후 Refresh는 하지 않는다 - SMO 추출이 방금 받은 변경을 덮어쓴다."
```

---

## Task 2: `ObjectHistoryViewModel`

**Files:**
- Create: `src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs` (신규)

**Interfaces:**
- Consumes: `IGitManager.GetHistory(string serverName, string databaseName, string relativeFilePath) → IReadOnlyList<CommitInfo>`, `CommitInfo`(`Sha`, `Message`, `Author`, `Date`)
- Produces:
  - `ObjectHistoryViewModel(IGitManager gitManager)`, `Entries` (`ObservableCollection<HistoryEntryViewModel>`), `IsEmpty` (`bool`), `Load(string?, string?, string?)`
  - `HistoryEntryViewModel` (`ShortSha`, `Message`, `Author`, `Date` — 모두 `string`)

- [x] **Step 1: 실패하는 테스트를 작성**

`tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs`를 새로 만든다.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class ObjectHistoryViewModelTests
    {
        private const string Server = "LocalServer";
        private const string Database = "SalesDB";
        private const string RelativePath = "dbo/Tables/Users.sql";

        private Mock<IGitManager> _git = null!;

        [SetUp]
        public void SetUp()
        {
            _git = new Mock<IGitManager>();
            _git.Setup(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new List<CommitInfo>());
        }

        private ObjectHistoryViewModel NewViewModel() => new ObjectHistoryViewModel(_git.Object);

        private static CommitInfo Commit(string sha, string message, string author = "Tester")
            => new CommitInfo
            {
                Sha = sha,
                Message = message,
                Author = author,
                Date = new DateTimeOffset(2026, 8, 1, 14, 30, 0, TimeSpan.Zero)
            };

        private void GivenHistory(params CommitInfo[] commits)
        {
            _git.Setup(g => g.GetHistory(Server, Database, RelativePath)).Returns(commits.ToList());
        }

        // ---------- 변환 ----------

        [Test]
        public void Load_ShortensTheShaToSevenCharacters()
        {
            GivenHistory(Commit("a3f9c2b1d4e5f60718293a4b5c6d7e8f90123456", "인덱스 추가"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().ShortSha, Is.EqualTo("a3f9c2b"));
        }

        [Test]
        public void Load_KeepsAShaShorterThanSevenCharactersAsIs()
        {
            GivenHistory(Commit("abc12", "짧은 해시"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().ShortSha, Is.EqualTo("abc12"));
        }

        [Test]
        public void Load_ShowsOnlyTheFirstLineOfTheCommitMessage()
        {
            GivenHistory(Commit("abc1234567", "제목 줄\n\n본문 설명이 이어진다"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().Message, Is.EqualTo("제목 줄"),
                "목록 한 행에 여러 줄이 들어가면 표가 무너집니다");
        }

        [Test]
        public void Load_FormatsTheDate()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().Date, Is.EqualTo("2026-08-01 14:30"));
        }

        [Test]
        public void Load_KeepsTheOrderGitReturned()
        {
            GivenHistory(
                Commit("1111111111", "최신"),
                Commit("2222222222", "이전"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Select(e => e.Message), Is.EqualTo(new[] { "최신", "이전" }),
                "GitManager.GetHistory가 최신순으로 주므로 그대로 보여줍니다");
        }

        // ---------- 목록 상태 ----------

        [Test]
        public void Load_ReplacesThePreviousEntries()
        {
            GivenHistory(Commit("1111111111", "첫 조회"));
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);

            GivenHistory(Commit("2222222222", "두 번째 조회"));
            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Select(e => e.Message), Is.EqualTo(new[] { "두 번째 조회" }),
                "다른 객체를 선택했을 때 이전 객체의 이력이 남으면 안 됩니다");
        }

        [Test]
        public void IsEmpty_IsTrue_BeforeAnyLoad()
        {
            Assert.That(NewViewModel().IsEmpty, Is.True);
        }

        [Test]
        public void IsEmpty_IsFalse_WhenHistoryExists()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.IsEmpty, Is.False);
        }

        [Test]
        public void IsEmpty_RaisesPropertyChanged_OnLoad()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Load(Server, Database, RelativePath);

            Assert.That(raised, Does.Contain(nameof(ObjectHistoryViewModel.IsEmpty)),
                "안내 문구의 표시 여부가 이 알림에 걸려 있습니다");
        }

        // ---------- 인자 검증 ----------

        [TestCase(null, Database, RelativePath)]
        [TestCase(Server, null, RelativePath)]
        [TestCase(Server, Database, null)]
        [TestCase(Server, Database, "   ")]
        public void Load_DoesNotQueryGit_WhenAnArgumentIsMissing(string? server, string? database, string? path)
        {
            var vm = NewViewModel();

            vm.Load(server, database, path);

            Assert.That(vm.Entries, Is.Empty);
            _git.Verify(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void Load_ClearsTheList_WhenTheSelectionGoesAway()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);

            vm.Load(Server, Database, null);

            Assert.That(vm.Entries, Is.Empty);
            Assert.That(vm.IsEmpty, Is.True);
        }
    }
}
```

- [x] **Step 2: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ObjectHistoryViewModelTests"
```

Expected: 컴파일 실패 — `ObjectHistoryViewModel` 형식을 찾을 수 없음(CS0246)

- [x] **Step 3: `ObjectHistoryViewModel`을 구현**

`src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs`를 새로 만든다.

`GetHistory`는 non-nullable로 선언되어 있지만 아래 코드는 `null`을 방어한다 — Moq은 스텁하지 않은 호출에 `null`을 돌려주고, 그 경우 `foreach`가 터진다. 이 방어 때문에 컴파일 경고가 나면 방어를 빼고 테스트 `SetUp`의 스텁에만 의존하도록 바꾼 뒤 보고하라. 빌드 출력에 경고를 남기는 쪽이 더 나쁘다.

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>
    /// 선택된 객체의 커밋 이력을 보여준다. (Feature 7)
    /// ViewChangesViewModel이 이미 크므로 이력 로직은 처음부터 여기에 둔다.
    /// </summary>
    public class ObjectHistoryViewModel : INotifyPropertyChanged
    {
        private readonly IGitManager _gitManager;

        public ObjectHistoryViewModel(IGitManager gitManager)
        {
            _gitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
        }

        public ObservableCollection<HistoryEntryViewModel> Entries { get; } = new ObservableCollection<HistoryEntryViewModel>();

        /// <summary>비어 있으면 화면이 목록 대신 안내 문구를 보여준다.</summary>
        public bool IsEmpty => Entries.Count == 0;

        /// <summary>
        /// 해당 객체의 이력을 다시 읽는다. 인자가 하나라도 비면 목록을 비운 상태로 끝낸다.
        /// </summary>
        public void Load(string? serverName, string? databaseName, string? relativePath)
        {
            Entries.Clear();

            if (!string.IsNullOrWhiteSpace(serverName)
                && !string.IsNullOrWhiteSpace(databaseName)
                && !string.IsNullOrWhiteSpace(relativePath))
            {
                var history = _gitManager.GetHistory(serverName!, databaseName!, relativePath!)
                    ?? (IReadOnlyList<CommitInfo>)new List<CommitInfo>();

                foreach (var commit in history)
                {
                    if (commit == null) continue;
                    Entries.Add(HistoryEntryViewModel.From(commit));
                }
            }

            OnPropertyChanged(nameof(IsEmpty));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 이력 목록의 한 행.
    /// SHA 축약과 날짜 서식은 화면 관심사이므로 Core의 <see cref="CommitInfo"/>에 두지 않는다.
    /// </summary>
    public class HistoryEntryViewModel
    {
        private const int ShortShaLength = 7;

        public string ShortSha { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;

        public static HistoryEntryViewModel From(CommitInfo commit)
        {
            return new HistoryEntryViewModel
            {
                ShortSha = Shorten(commit.Sha),
                Message = FirstLine(commit.Message),
                Author = commit.Author ?? string.Empty,
                Date = commit.Date.ToString("yyyy-MM-dd HH:mm")
            };
        }

        private static string Shorten(string? sha)
        {
            if (string.IsNullOrEmpty(sha)) return string.Empty;
            return sha!.Length > ShortShaLength ? sha.Substring(0, ShortShaLength) : sha!;
        }

        /// <summary>커밋 메시지는 여러 줄일 수 있다. 목록에는 첫 줄만 보여준다.</summary>
        private static string FirstLine(string? message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            var index = message!.IndexOfAny(new[] { '\r', '\n' });
            return (index < 0 ? message! : message!.Substring(0, index)).Trim();
        }
    }
}
```

- [x] **Step 4: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ObjectHistoryViewModelTests"
```

Expected: PASS (14개)

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ObjectHistoryViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ObjectHistoryViewModelTests.cs
git commit -m "feat(vsix): 객체 커밋 이력 ViewModel (Feature 7)

SHA 축약과 날짜 서식은 화면 관심사이므로 Core 모델이 아니라
표시용 HistoryEntryViewModel에서 처리한다."
```

---

## Task 3: `ViewChangesViewModel`에 이력 연결

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`

**Interfaces:**
- Consumes: Task 2의 `ObjectHistoryViewModel(IGitManager)`, `Load(string?, string?, string?)`, `Entries`, `IsEmpty`
- Produces: `ViewChangesViewModel.History` (`ObjectHistoryViewModel`) — Task 4의 XAML이 `History.Entries`와 `History.IsEmpty`에 바인딩한다

**배경:** 지금은 목록을 비워도 `SelectedChange`가 그대로 남는다. 목록에 없는 객체가 선택된 상태로 남으면 Diff와 이력이 실재하지 않는 대상을 가리킨다.

- [x] **Step 1: 실패하는 테스트를 작성**

`tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs`의 `// ---------- Pull ----------` 주석 바로 앞에 추가한다.

```csharp
        // ---------- 객체 이력 ----------

        [Test]
        public void SelectedChange_LoadsTheHistoryOfTheSelectedObject()
        {
            _git.Setup(g => g.GetHistory(Server, Database, "dbo/Tables/Users.sql"))
                .Returns(new List<CommitInfo>
                {
                    new CommitInfo { Sha = "a3f9c2b1d4", Message = "인덱스 추가", Author = "Tester", Date = DateTimeOffset.Now }
                });
            var vm = NewConnectedViewModel();

            vm.SelectedChange = new ChangeItemViewModel
            {
                ObjectName = "dbo.Users",
                RelativePath = "dbo/Tables/Users.sql"
            };

            Assert.That(vm.History.Entries, Has.Count.EqualTo(1));
            Assert.That(vm.History.Entries[0].ShortSha, Is.EqualTo("a3f9c2b"));
        }

        [Test]
        public void SelectedChange_ClearsTheHistory_WhenTheSelectionIsCleared()
        {
            _git.Setup(g => g.GetHistory(Server, Database, "dbo/Tables/Users.sql"))
                .Returns(new List<CommitInfo>
                {
                    new CommitInfo { Sha = "a3f9c2b1d4", Message = "인덱스 추가", Author = "Tester", Date = DateTimeOffset.Now }
                });
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };

            vm.SelectedChange = null;

            Assert.That(vm.History.Entries, Is.Empty);
        }

        [Test]
        public void Refresh_ClearsTheSelection()
        {
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };

            vm.Refresh();

            Assert.That(vm.SelectedChange, Is.Null,
                "목록을 비웠는데 선택이 남으면 Diff와 이력이 목록에 없는 객체를 가리킵니다");
        }

        [Test]
        public void SetContext_ClearsTheSelection()
        {
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };

            vm.SetContext(Server, Database);

            Assert.That(vm.SelectedChange, Is.Null);
        }
```

같은 파일의 `SetUp`에 기본 스텁을 추가한다. 이것이 없으면 Moq이 `null`을 돌려주고, 이력을 검사하지 않는 다른 테스트들이 예상치 못한 곳에서 흔들린다.

```csharp
            _git.Setup(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new List<CommitInfo>());
```

- [x] **Step 2: 테스트가 실패하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: 컴파일 실패 — `History` 정의 없음(CS1061)

- [x] **Step 3: `History` 속성을 추가하고 선택 변경에 연결**

`src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`에서 네 곳을 고친다.

(a) 생성자에서 `_scriptExporter = ...` 다음 줄에 추가:

```csharp
            History = new ObjectHistoryViewModel(_gitManager);
```

(b) `SelectedChange` 속성 전체를 다음으로 바꾼다:

```csharp
        private ChangeItemViewModel? _selectedChange;
        public ChangeItemViewModel? SelectedChange
        {
            get => _selectedChange;
            set
            {
                if (ReferenceEquals(_selectedChange, value)) return;
                _selectedChange = value;
                OnPropertyChanged();
                History.Load(ServerName, DatabaseName, value?.RelativePath);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>선택된 객체의 커밋 이력. (Feature 7)</summary>
        public ObjectHistoryViewModel History { get; }
```

(c) `SetContext`의 `Changes.Clear();` 다음 줄에 추가:

```csharp
            SelectedChange = null;
```

(d) `Refresh()`의 `Changes.Clear();` 다음 줄에 추가:

```csharp
            SelectedChange = null;
```

- [x] **Step 4: 테스트가 통과하는지 확인**

```bash
dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~ViewChangesViewModelTests"
```

Expected: PASS (신규 4개 포함, 기존 테스트 회귀 없음)

- [x] **Step 5: 전체 테스트로 회귀 확인**

```bash
dotnet build DBVC.slnx && dotnet test DBVC.slnx -f net10.0
```

Expected: 빌드 성공, 전부 통과

- [x] **Step 6: 커밋**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "feat(vsix): 선택된 객체의 이력을 ViewChangesViewModel에 연결

목록을 비울 때 선택도 함께 해제한다. 남아 있으면 Diff와 이력이
목록에 없는 객체를 가리킨다."
```

---

## Task 4: 화면 배치 (`WrapPanel` + `TabControl`)

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml`

**Interfaces:**
- Consumes: Task 1의 `PullCommand`, Task 3의 `History.Entries`·`History.IsEmpty`, 기존 `IsInitialized`·`HasWarning`·`IsMapped` 등

이 태스크에는 자동화 테스트가 없다. WPF 레이아웃은 CI에서 검증할 수 없으며, README가 이미 "CI로 검증되지 않는 것"으로 분류한 범주다.

- [x] **Step 1: XAML을 교체**

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

            <!--
                Top Area. 버튼이 늘어 좁게 도킹하면 잘리므로 WrapPanel로 줄바꿈되게 한다.
            -->
            <WrapPanel Grid.Row="0" Orientation="Horizontal" Margin="5">
                <Button Content="Refresh" Command="{Binding RefreshCommand}" Width="70" Margin="0,0,10,4"/>
                <TextBox Text="{Binding CommitMessage, UpdateSourceTrigger=PropertyChanged}"
                         Width="240" Margin="0,0,10,4"
                         IsEnabled="{Binding IsMapped}"/>
                <Button Content="Commit" Command="{Binding CommitCommand}" Width="70" Margin="0,0,10,4" />
                <Button Content="Pull" Command="{Binding PullCommand}" Width="70" Margin="0,0,16,4"
                        ToolTip="원격 저장소의 변경을 로컬 저장소로 가져옵니다. 데이터베이스에는 적용하지 않습니다." />
                <Button Content="Deployment Script" Command="{Binding GenerateDeploymentScriptCommand}" Width="130" Margin="0,0,6,4"
                        ToolTip="선택한 객체의 현재 DDL을 단일 .sql 파일로 병합합니다." />
                <Button Content="Rollback Script" Command="{Binding GenerateRollbackScriptCommand}" Width="120" Margin="0,0,0,4"
                        ToolTip="선택한 객체가 마지막으로 커밋되기 직전 코드를 단일 .sql 파일로 병합합니다." />
            </WrapPanel>

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

            <!-- Bottom Area: 선택된 객체에 대한 두 가지 뷰 -->
            <TabControl Grid.Row="3">
                <TabItem Header="Diff">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="5" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>

                        <avalonEdit:TextEditor x:Name="OldTextEditor" Grid.Column="0" IsReadOnly="True" SyntaxHighlighting="TSQL" />
                        <GridSplitter Grid.Column="1" Width="5" HorizontalAlignment="Stretch" />
                        <avalonEdit:TextEditor x:Name="NewTextEditor" Grid.Column="2" IsReadOnly="True" SyntaxHighlighting="TSQL" />
                    </Grid>
                </TabItem>

                <TabItem Header="History">
                    <Grid>
                        <ListView ItemsSource="{Binding History.Entries}">
                            <ListView.View>
                                <GridView>
                                    <GridViewColumn Header="Date" Width="130" DisplayMemberBinding="{Binding Date}"/>
                                    <GridViewColumn Header="Author" Width="110" DisplayMemberBinding="{Binding Author}"/>
                                    <GridViewColumn Header="Message" Width="320" DisplayMemberBinding="{Binding Message}"/>
                                    <GridViewColumn Header="SHA" Width="80" DisplayMemberBinding="{Binding ShortSha}"/>
                                </GridView>
                            </ListView.View>
                        </ListView>

                        <TextBlock Text="이력이 없습니다." Foreground="#808080"
                                   HorizontalAlignment="Center" VerticalAlignment="Center"
                                   Visibility="{Binding History.IsEmpty, Converter={StaticResource BoolToVis}}"/>
                    </Grid>
                </TabItem>
            </TabControl>
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

- [x] **Step 2: 빌드가 통과하는지 확인**

```bash
dotnet build DBVC.slnx
```

Expected: 빌드 성공. `x:Name="OldTextEditor"`와 `x:Name="NewTextEditor"`가 유지되어야 코드비하인드가 컴파일된다.

- [x] **Step 3: 두 이름이 살아 있는지 확인**

```bash
grep -c 'x:Name="OldTextEditor"\|x:Name="NewTextEditor"' src/DBVC.Vsix/UI/ViewChangesControl.xaml
```

Expected: `2`

- [x] **Step 4: 전체 테스트로 회귀 확인**

```bash
dotnet test DBVC.slnx -f net10.0
```

Expected: 전부 통과

- [x] **Step 5: 커밋**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml
git commit -m "feat(vsix): Pull 버튼과 History 탭 배치

하단을 [Diff][History] 탭으로 나누고, 버튼이 늘어난 액션 영역은
좁은 도킹에서 잘리지 않도록 WrapPanel로 바꾼다."
```

---

## Task 5: 문서 갱신

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: Task 1~4의 최종 동작

- [x] **Step 1: 주요 기능 목록에 두 항목을 추가**

`README.md`의 "SQL 에디터 컨텍스트 메뉴" 항목(주요 기능 목록의 마지막) 다음에 두 줄을 추가한다.

```markdown
- **Git Pull:** 원격 저장소의 변경을 로컬 저장소로 가져옵니다. 충돌이 발생하면 병합을 중단하고 되돌립니다. 받은 스크립트를 데이터베이스에 적용할지는 사용자가 판단합니다.
- **객체 이력:** 선택한 객체의 커밋 이력(날짜·작성자·메시지·SHA)을 하단 History 탭에서 확인할 수 있습니다.
```

- [x] **Step 2: 기능 커버리지 문구를 갱신**

`README.md`의 "### 기능 커버리지" 절 첫 문장을 다음으로 바꾼다.

```markdown
14개 MVP 기능 중 13개가 구현되어 있습니다. **Object Explorer 상태 아이콘 오버레이(Feature 10)는 미구현**입니다.
```

- [x] **Step 3: 사용법에 Pull과 이력 확인을 추가**

`README.md`의 "6. **Git 커밋:**" 항목 다음에 두 항목을 추가한다.

```markdown
7. **원격 변경 가져오기:** **"Pull"** 버튼을 누르면 원격 저장소의 변경을 로컬 저장소로 가져옵니다. 커밋하지 않은 변경이 있으면 먼저 확인을 받습니다 — 충돌이 발생하면 병합을 되돌리면서 그 변경도 함께 사라지기 때문입니다(Refresh로 다시 추출할 수 있습니다). Pull은 파일만 가져올 뿐 데이터베이스에 적용하지 않으므로, 받은 스크립트를 확인한 뒤 필요하면 직접 실행하세요.
8. **객체 이력 확인:** 목록에서 객체를 선택하고 하단의 **History** 탭을 열면 그 객체의 `.sql` 파일을 변경한 커밋들이 최신순으로 표시됩니다.
```

- [x] **Step 4: 문서가 실제 동작과 맞는지 확인**

`README.md`를 처음부터 끝까지 읽고 다음을 확인한다.

- 사용법 항목 번호가 1부터 8까지 연속인지
- Pull이 데이터베이스에 적용되지 않는다는 점이 본문과 어긋나지 않는지

```bash
dotnet test DBVC.slnx -f net10.0
```

Expected: 전부 통과 (문서 변경이 테스트에 영향을 주지 않음을 확인)

- [x] **Step 5: 커밋**

```bash
git add README.md
git commit -m "docs: Pull과 객체 이력 사용법 추가

MVP 14개 중 13개 구현 상태를 반영한다."
```

---

## 완료 후 수동 검증 (SSMS 21 필요)

CI가 검증하지 못하는 항목이다. Windows + SSMS 21 환경에서 확인한다.

- [x] 작업 트리가 깨끗한 상태에서 Pull → 확인 없이 실행되고 완료 알림이 뜬다
- [x] Refresh 후(추출물이 남은 상태) Pull → 무엇이 사라질 수 있는지 확인 대화상자가 뜬다
- [x] 그 대화상자에서 취소 → 아무 일도 일어나지 않는다
- [x] 작업 트리가 더러운 상태에서, 그 변경이 원격의 들어오는 변경과 **같은 파일**을 건드릴 때 Pull → 확인 대화상자에서 계속 → 실제로 무슨 일이 일어나는지(성공/거부/충돌 후 되돌림)와 그 결과 메시지가 이해할 수 있는 내용인지 확인한다
- [x] 원격이 없는 저장소에서 Pull → 오류 메시지가 뜨고 아무것도 바뀌지 않는다
- [x] Pull 성공 직후 목록이 자동으로 새로고침되지 **않는다** (받은 변경이 유지된다)
- [x] 확인·완료 대화상자가 SSMS 창 뒤로 숨지 않는다
- [x] 객체를 선택하고 History 탭 → 커밋 목록이 최신순으로 보인다
- [x] 이력이 없는 신규 객체를 선택 → "이력이 없습니다."가 보인다
- [x] History 탭에서 Diff 탭으로 돌아가면 배경색 강조가 그대로 보인다 (`TabControl`이 비활성 탭 콘텐츠를 시각 트리에서 분리하므로 확인이 필요하다)
- [x] 도구 창을 좁게 도킹 → 액션 영역 버튼이 잘리지 않고 줄바꿈된다
