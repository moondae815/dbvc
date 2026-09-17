# 도구 창 버튼 묶기 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 개발 클론 화면에 흩어진 버튼 14개를 대상·동기화·커밋·체크한 항목 네 무리로 묶고, 가끔 쓰는 셋(전체 다시 추출, 배포/롤백 스크립트, 브랜치 조작)을 드롭다운 메뉴로 접는다.

**Architecture:** 명령과 `CanExecute`는 그대로다. ViewModel에는 화면이 읽는 값 셋(`CheckedCount`, `PullButtonText`/`PushButtonText`와 그 근거 `LastRemoteStatus`)만 더한다. 드롭다운은 버튼에 붙은 `ContextMenu`를 `DropDownMenu` 도우미가 준비해 여는 방식이며, 배치는 `ViewChangesControl.xaml`에서만 바뀐다.

**Tech Stack:** C# / .NET Framework 4.8, WPF(MVVM), NUnit, Moq

**Spec:** [`docs/superpowers/specs/2026-09-17-dbvc-toolbar-consolidation-design.md`](../specs/2026-09-17-dbvc-toolbar-consolidation-design.md)

## Global Constraints

- **사용자에게 보이는 모든 문구는 한국어다.** 버튼, 메뉴 항목, ToolTip 포함. `Pull`·`Push`·`Commit`은 지금도 영문 버튼명이라 그대로 둔다.
- **주석은 "왜"만 적는다.** 한국어 평서문, 함정과 근거를 남기는 기존 문체.
- **테스트 이름은 영어 `Method_Result_WhenCondition`.**
- **커밋 메시지는 한국어 명령형 현재시제 + 스코프.** 예: `feat(vsix): 체크한 항목 수를 낸다`. 끝에 빈 줄 뒤 `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- **TDD:** 실패하는 테스트 → 최소 구현 → 통과 확인 → 커밋.
- **Core와 `MappingPolicy`를 바꾸지 않는다.** 명령을 더하거나 `CanExecute`를 고치지 않는다(스펙 머리말).
- **패키지·참조를 더하지 않는다.** `Microsoft.VisualStudio.Imaging`(아이콘)도 들이지 않는다(스펙 2.6).
- **우클릭 메뉴를 만들지 않는다**(스펙 2.3). 드롭다운 버튼에는 `ContextMenuService.IsEnabled="False"`를 준다 — 우클릭으로 열리면 `DropDownMenu.Prepare`를 거치지 않아 항목이 눌려도 아무 일이 없다.
- **체크 작업 줄은 항상 보인다.** 잠금은 명령의 `CanExecute`가 맡고 화면에 `IsEnabled`를 따로 걸지 않는다(스펙 2.4). 예외는 명령이 없는 드롭다운 버튼 셋뿐이며 `IsEnabled="{Binding IsNotBusy}"`로 잠근다(스펙 3.2의 2).
- **원격 확인은 누를 때만 돈다.** 숫자를 자동으로 채우는 코드를 넣지 않는다(스펙 2.5).
- **배포·감사 패널은 건드리지 않는다**(스펙 2.7). `DeploymentPanelGrid` 안쪽 XAML(0.8.0의 병합 영역 포함), `DeploymentViewModel`, `UnmergedBranchItemViewModel`을 고치지 않는다. 두 화면이 맞닿는 곳은 맨 윗줄뿐이다.
- **버전은 0.8.0(병합) → 0.9.0**이다. 병합 문서의 버전은 고치지 않는다.
- **도구 줄 컨테이너에 `TextElement.Foreground`를 걸지 않는다.** 안에 든 `TextBox`까지 상속되어 어두운 테마에서 흰 바탕에 흰 글씨가 된다. 글자를 가진 `TextBlock`·`CheckBox`에만 직접 준다.
- 빌드·테스트 명령:
  - `dotnet build DBVC.slnx`
  - ViewModel 테스트: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~<이름>"`
  - 레이아웃 테스트(`#if NETFRAMEWORK`라 net48에서만 돈다): `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~<이름>"`

## 파일 구조

| 파일 | 책임 |
| --- | --- |
| `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` | `CheckedCount`, 항목 체크 구독, `LastRemoteStatus`·`PullButtonText`·`PushButtonText` |
| `src/DBVC.Vsix/UI/DropDownMenu.cs` (신규) | 버튼에 붙은 `ContextMenu`를 드롭다운으로 준비·열기. 세 메뉴가 같은 함정을 공유하므로 한 곳 |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml` | 대상 줄의 브랜치 메뉴 버튼, 변경 목록 위 세 줄(동기화·커밋·체크) |
| `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` | 드롭다운 버튼 셋이 함께 쓰는 `Click` 처리기 하나 |
| `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` | 체크 개수·원격 숫자 테스트 |
| `tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs` | 브랜치 메뉴 버튼 위치·숨김 |
| `tests/DBVC.Vsix.Tests/UI/DropDownMenuTests.cs` (신규) | 세 메뉴의 배선·색·우클릭 차단 |
| `tests/DBVC.Vsix.Tests/UI/ChangeListToolbarLayoutTests.cs` (신규) | 동기화·커밋·체크 줄의 배치 |
| `tests/DBVC.Vsix.Tests/UI/ViewChangesControlFixtures.cs` | 새 레이아웃 테스트가 쓰는 `LayoutAt`·`TopLeftOf`·`Find` |
| `README.md`, `docs/user-guide.html`, `docs/setup-checklist.md`, `src/DBVC.Vsix/source.extension.vsixmanifest` | 문서·버전(0.9.0) |
| `DeploymentPanelGrid` 안쪽 XAML, `DeploymentViewModel.cs`, `UnmergedBranchItemViewModel.cs` | **건드리지 않는다**(스펙 2.7) — Task 4의 배포 모드 테스트와 Task 5 Step 5의 diff 확인이 지킨다 |

---

### Task 1: 체크 변경을 듣고 체크한 항목 수를 낸다

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (using 목록, 생성자 끝, `Changes` 선언 785행 부근)
- Test: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` (`// ---------- Commit ----------` 절 바로 앞)

**Interfaces:**
- Produces: `public int CheckedCount { get; }` — Task 4의 `CheckedCountLabel`이 바인딩한다.

**조사 결과(스펙 3.3이 "확인하지 않았다"고 남긴 것).** `RelayCommand`는 `CommandManager.RequerySuggested`를 쓰지 않고 `RaiseCanExecuteChanged()`를 불러야만 `CanExecuteChanged`를 낸다. ViewModel은 `ChangeItemViewModel.PropertyChanged`를 구독하지 않는다. 따라서 **지금은 체크를 모두 풀어도 Commit·되돌리기·무시·스크립트 버튼이 다른 일이 일어날 때까지 켜진 채로 남는다.** 아래 Step 1의 세 번째 테스트가 그것을 드러내며 실패해야 한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`ViewChangesViewModelTests.cs`의 `// ---------- Commit ----------` 줄 바로 앞에 넣는다.

```csharp
        // ---------- 체크한 항목 ----------

        private ViewChangesViewModel NewViewModelWithThreeChanges()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"),
                Record("dbo", "Orders", "Modified", "dbo/Tables/Orders.sql"),
                Record("dbo", "Items", "Modified", "dbo/Tables/Items.sql")
            });
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            Assert.That(vm.Changes.Count, Is.EqualTo(3), "전제: 새로고침이 세 항목을 채워야 합니다");
            return vm;
        }

        [Test]
        public void CheckedCount_CountsOnlyCheckedItems_WhenSomeAreUnchecked()
        {
            var vm = NewViewModelWithThreeChanges();
            Assert.That(vm.CheckedCount, Is.EqualTo(3), "새로고침 직후에는 모두 체크되어 있습니다");

            vm.Changes.Single(c => c.ObjectName == "dbo.Orders").IsSelected = false;

            Assert.That(vm.CheckedCount, Is.EqualTo(2));
        }

        [Test]
        public void CheckedCount_RaisesPropertyChanged_WhenAnItemIsUnchecked()
        {
            var vm = NewViewModelWithThreeChanges();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Changes[0].IsSelected = false;

            Assert.That(raised, Does.Contain(nameof(ViewChangesViewModel.CheckedCount)),
                "알리지 않으면 '체크한 항목 n개'가 체크를 바꿔도 그대로 남습니다");
        }

        [Test]
        public void DiscardCommand_RaisesCanExecuteChanged_WhenAnItemIsUnchecked()
        {
            // RelayCommand는 RequerySuggested를 쓰지 않는다. 체크는 명령을 거치지 않고 바인딩으로
            // 바로 바뀌므로, 여기서 알리지 않으면 체크를 다 풀어도 버튼이 켜진 채 남는다.
            var vm = NewViewModelWithThreeChanges();
            var discardRaised = 0;
            var commitRaised = 0;
            var scriptRaised = 0;
            vm.DiscardCommand.CanExecuteChanged += (_, __) => discardRaised++;
            vm.CommitCommand.CanExecuteChanged += (_, __) => commitRaised++;
            vm.GenerateDeploymentScriptCommand.CanExecuteChanged += (_, __) => scriptRaised++;

            foreach (var item in vm.Changes) item.IsSelected = false;

            Assert.That(discardRaised, Is.GreaterThan(0));
            Assert.That(commitRaised, Is.GreaterThan(0));
            Assert.That(scriptRaised, Is.GreaterThan(0));
            Assert.That(vm.DiscardCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void CheckedCount_IsZero_AfterTheTargetChanges()
        {
            var vm = NewViewModelWithThreeChanges();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S2", "D2", SqlAuthMode.Windows, null, null, null));
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.CheckedCount, Is.EqualTo(0));
            Assert.That(raised, Does.Contain(nameof(ViewChangesViewModel.CheckedCount)),
                "Clear도 알려야 합니다 - 이전 대상의 개수가 화면에 남습니다");
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~CheckedCount|FullyQualifiedName~DiscardCommand_RaisesCanExecuteChanged_WhenAnItemIsUnchecked"`
Expected: 컴파일 실패 — `'ViewChangesViewModel' does not contain a definition for 'CheckedCount'`.

컴파일 오류만으로는 "체크 변경이 버튼 잠금을 다시 판정하지 않는다"는 조사 결과를 확인하지 못한다. 그래서 Step 3을 두 번에 나눈다: 먼저 `public int CheckedCount => Changes.Count(c => c.IsSelected);` 한 줄만 더하고 다시 돌린다.

Expected: `CheckedCount_CountsOnlyCheckedItems_WhenSomeAreUnchecked`는 PASS, 나머지 셋은 FAIL(`discardRaised`가 0, `raised`에 `CheckedCount` 없음). `DiscardCommand_RaisesCanExecuteChanged_WhenAnItemIsUnchecked`가 이 시점에 통과한다면 조사가 틀린 것이다 — 멈추고 어디서 이미 알리고 있는지 찾아 보고한다(스펙 3.3: 이미 갱신되고 있으면 이중으로 부르지 않는다).

- [ ] **Step 3: 구현한다**

`ViewChangesViewModel.cs` 맨 위 using에 더한다(알파벳 순서 자리, `System.Collections.ObjectModel` 다음):

```csharp
using System.Collections.Specialized;
```

`public ObservableCollection<ChangeItemViewModel> Changes { get; } = ...;` 선언 바로 아래에 더한다(Step 2에서 먼저 넣은 `CheckedCount` 한 줄은 아래 블록의 것으로 바꿔 한 번만 남긴다):

```csharp
        /// <summary>체크한 항목 수. 체크 작업 줄의 "체크한 항목 n개"가 읽는다.</summary>
        public int CheckedCount => Changes.Count(c => c.IsSelected);

        private void OnChangesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (ChangeItemViewModel item in e.NewItems)
                {
                    item.PropertyChanged += OnChangeItemPropertyChanged;
                }
            }

            // Clear(Reset)는 OldItems를 주지 않아 구독을 풀 수 없다. 풀지 않아도 새지 않는다 -
            // 항목이 ViewModel을 붙드는 방향이지 그 반대가 아니므로, 목록에서 빠진 항목은 그대로 수거된다.
            if (e.OldItems != null)
            {
                foreach (ChangeItemViewModel item in e.OldItems)
                {
                    item.PropertyChanged -= OnChangeItemPropertyChanged;
                }
            }

            OnPropertyChanged(nameof(CheckedCount));
        }

        /// <summary>
        /// 체크는 명령을 거치지 않고 바인딩으로 바로 바뀐다. RelayCommand는 RequerySuggested를 쓰지
        /// 않으므로, 여기서 알리지 않으면 체크를 다 풀어도 체크에 기대는 버튼이 켜진 채 남는다.
        ///
        /// RaiseActionCanExecuteChanged 전체를 부르지 않는다 - 거기에 든 CanPush는 저장소를 읽으므로
        /// 체크박스를 누를 때마다 디스크를 건드리게 된다. 체크를 판정에 쓰는 명령만 알린다.
        /// </summary>
        private void OnChangeItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ChangeItemViewModel.IsSelected)) return;

            OnPropertyChanged(nameof(CheckedCount));
            (CommitCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateCommitMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DiscardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (IgnoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateDeploymentScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GenerateRollbackScriptCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
```

생성자에서 `ShowWholeRepositoryHistoryCommand = new RelayCommand(...)` 문장이 끝난 바로 다음 줄에 구독을 건다:

```csharp
            Changes.CollectionChanged += OnChangesCollectionChanged;
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~CheckedCount|FullyQualifiedName~DiscardCommand_RaisesCanExecuteChanged_WhenAnItemIsUnchecked"`
Expected: 4개 PASS.

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: 전체 PASS(기존 테스트 회귀 없음).

- [ ] **Step 5: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs
git commit -m "fix(vsix): 체크를 바꾸면 체크에 기대는 버튼의 잠금을 다시 판정한다" -m "체크한 항목 수(CheckedCount)도 함께 낸다." -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: 원격 확인 숫자를 Pull·Push 버튼 글자로 옮긴다

**Files:**
- Modify: `src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs` (`RemoteStatusText` 속성 929~941행, 대입 여섯 자리)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (`RemoteStatusLabel` 62~67행, Pull·Push 버튼 205~208행)
- Modify: `tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs` (`// ---------- 원격 확인 ----------` 절, `SwitchBranchCommand_ClearsStaleRemoteStatus_AfterASuccessfulSwitch`)
- Modify: `tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs` (`RemoteStatusLabel_*` 두 테스트 삭제)
- Modify: `tests/DBVC.Vsix.Tests/UI/ViewChangesControlFixtures.cs` (주석 한 줄, 도우미 셋 추가)
- Create: `tests/DBVC.Vsix.Tests/UI/ChangeListToolbarLayoutTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public RemoteStatus? LastRemoteStatus { get; }`
  - `public string PullButtonText { get; }` — `"Pull"` 또는 `"Pull ↓{BehindBy}"`
  - `public string PushButtonText { get; }` — `"Push"` 또는 `"Push ↑{AheadBy}"`
  - XAML 이름 `PullButton`, `PushButton` — Task 4가 자리를 옮긴다.
  - `ViewChangesControlFixtures.LayoutAt(ViewChangesControl, double)`, `TopLeftOf(ViewChangesControl, string)`, `Find<T>(ViewChangesControl, string)` — Task 3·4의 테스트가 쓴다.
- 없어지는 것: `RemoteStatusText`, `HasRemoteStatus`, XAML `RemoteStatusLabel`.

`RemoteStatus`의 생성자 순서는 `(aheadBy, behindBy)`다. `new RemoteStatus(2, 1)`은 올릴 커밋 2개·받을 커밋 1개이므로 `Pull ↓1`·`Push ↑2`가 된다.

- [ ] **Step 1: ViewModel 테스트를 새 속성으로 고쳐 쓴다**

`ViewChangesViewModelTests.cs`의 `// ---------- 원격 확인 ----------` 절에서 처음 네 테스트(`CheckRemoteCommand_ShowsAheadAndBehindCounts_WhenTheRemoteAnswers`, `RemoteStatusText_IsEmpty_BeforeTheUserAsks`, `RemoteStatusText_IsCleared_WhenTheTargetChanges`, `RemoteStatusText_IsCleared_AfterASuccessfulPull`)와 `CheckRemoteCommand_ReportsTheReason_WhenTheRemoteCannotBeReached`를 아래로 통째로 바꾼다. `CheckRemoteCommand_IsDisabled_WhenTheRepositoryIsBlocked`는 그대로 둔다.

```csharp
        [Test]
        public void CheckRemoteCommand_PutsTheCountsOnPullAndPush_WhenTheRemoteAnswers()
        {
            // 생성자 순서는 (aheadBy, behindBy)다 - 올릴 커밋 2개, 받을 커밋 1개.
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 1));
            var vm = NewConnectedViewModel();

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(vm.PullButtonText, Is.EqualTo("Pull ↓1"));
            Assert.That(vm.PushButtonText, Is.EqualTo("Push ↑2"));
        }

        [Test]
        public void PullButtonText_ShowsZero_WhenCheckedAndNothingToPull()
        {
            // 0을 생략하면 "확인했고 받을 것이 없다"와 "확인하지 않았다"가 같은 글자가 된다.
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(0, 0));
            var vm = NewConnectedViewModel();

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(vm.PullButtonText, Is.EqualTo("Pull ↓0"));
            Assert.That(vm.PushButtonText, Is.EqualTo("Push ↑0"));
        }

        [Test]
        public void PullButtonText_HasNoCount_BeforeTheUserAsks()
        {
            // 누르기 전에는 숫자가 없다. 낡은 숫자를 최신인 척 보여주지 않기 위해서다.
            var vm = NewConnectedViewModel();

            Assert.That(vm.LastRemoteStatus, Is.Null);
            Assert.That(vm.PullButtonText, Is.EqualTo("Pull"));
            Assert.That(vm.PushButtonText, Is.EqualTo("Push"));
            _git.Verify(g => g.FetchRemoteStatus(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void PullButtonText_RaisesPropertyChanged_WhenTheRemoteIsChecked()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 1));
            var vm = NewConnectedViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(raised, Does.Contain(nameof(ViewChangesViewModel.PullButtonText)));
            Assert.That(raised, Does.Contain(nameof(ViewChangesViewModel.PushButtonText)));
        }

        [Test]
        public void LastRemoteStatus_IsCleared_WhenTheTargetChanges()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 1));
            var vm = NewConnectedViewModel();
            vm.CheckRemoteCommand.Execute(null);

            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S2", "D2", SqlAuthMode.Windows, null, null, null));
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.PullButtonText, Is.EqualTo("Pull"),
                "다른 대상의 원격 상태가 남으면 사용자가 엉뚱한 저장소의 숫자를 읽습니다");
        }

        [Test]
        public void LastRemoteStatus_IsCleared_AfterASuccessfulPull()
        {
            // Pull이 뒤처짐을 줄이므로 원격 확인이 보여준 숫자는 낡는다. 지우지 않으면
            // 다 받은 뒤에도 "Pull ↓3"이 최신인 척 남는다.
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(0, 3));
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();
            vm.CheckRemoteCommand.Execute(null);
            Assert.That(vm.PullButtonText, Is.EqualTo("Pull ↓3"), "선행 조건: 원격 확인으로 값을 채워 둔다");

            vm.PullCommand.Execute(null);

            Assert.That(vm.LastRemoteStatus, Is.Null);
            Assert.That(vm.PullButtonText, Is.EqualTo("Pull"));
        }

        [Test]
        public void LastRemoteStatus_IsCleared_AfterASuccessfulPush()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 0));
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.Pushed);
            var vm = NewConnectedViewModel();
            vm.CheckRemoteCommand.Execute(null);
            Assert.That(vm.PushButtonText, Is.EqualTo("Push ↑2"), "선행 조건: 원격 확인으로 값을 채워 둔다");

            vm.PushCommand.Execute(null);

            Assert.That(vm.PushButtonText, Is.EqualTo("Push"));
        }

        [Test]
        public void CheckRemoteCommand_ReportsTheReason_WhenTheRemoteCannotBeReached()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database))
                .Throws(new GitRemoteException("원격과 통신하지 못했습니다."));
            var vm = NewConnectedViewModel();

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Not.Empty);
            Assert.That(vm.LastRemoteStatus, Is.Null);
        }
```

같은 파일 `SwitchBranchCommand_ClearsStaleRemoteStatus_AfterASuccessfulSwitch`에서 두 곳을 고친다.

```csharp
            Assert.That(vm.HasRemoteStatus, Is.True, "전제: 확인한 숫자가 이미 떠 있어야 합니다");
```
→
```csharp
            Assert.That(vm.LastRemoteStatus, Is.Not.Null, "전제: 확인한 숫자가 이미 떠 있어야 합니다");
```

```csharp
            Assert.That(vm.RemoteStatusText, Is.Null,
```
→
```csharp
            Assert.That(vm.LastRemoteStatus, Is.Null,
```

그 테스트 주석의 `"브랜치: develop" 옆에`는 `develop 브랜치의 Pull·Push 버튼에`로 고친다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0 --filter "FullyQualifiedName~RemoteStatus|FullyQualifiedName~PullButtonText|FullyQualifiedName~CheckRemoteCommand|FullyQualifiedName~SwitchBranchCommand_ClearsStaleRemoteStatus"`
Expected: 컴파일 실패 — `'ViewChangesViewModel' does not contain a definition for 'PullButtonText'` / `'LastRemoteStatus'`.

- [ ] **Step 3: ViewModel을 구현한다**

`RemoteStatusText` 속성 블록(필드 `_remoteStatusText`, 속성 `RemoteStatusText`, `HasRemoteStatus`)을 통째로 아래로 바꾼다.

```csharp
        private RemoteStatus? _lastRemoteStatus;

        /// <summary>
        /// 마지막으로 원격을 확인한 결과. 누르기 전에는 <c>null</c>이다 —
        /// 낡은 숫자를 최신인 척 보여주는 것이 아무것도 안 보여주는 것보다 나쁘다.
        /// </summary>
        public RemoteStatus? LastRemoteStatus
        {
            get => _lastRemoteStatus;
            private set
            {
                if (ReferenceEquals(_lastRemoteStatus, value)) return;
                _lastRemoteStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PullButtonText));
                OnPropertyChanged(nameof(PushButtonText));
            }
        }

        /// <summary>
        /// 숫자를 그것이 가리키는 행동 옆에 둔다. 0이어도 붙인다 - "확인했고 받을 것이 없다"와
        /// "확인하지 않았다"를 가르는 것이 이 숫자의 목적이다.
        /// </summary>
        public string PullButtonText =>
            LastRemoteStatus == null ? "Pull" : $"Pull ↓{LastRemoteStatus.BehindBy}";

        /// <inheritdoc cref="PullButtonText"/>
        public string PushButtonText =>
            LastRemoteStatus == null ? "Push" : $"Push ↑{LastRemoteStatus.AheadBy}";
```

대입을 바꾼다. 먼저 자리를 확인한다:

Run: `grep -n "RemoteStatusText" src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
Expected: 7줄 — `null` 대입 여섯(대상 무효화, 확인 실패, Pull 성공, Push 성공, 브랜치 조작 후, 커밋 후)과 `$"받을 커밋 {status.BehindBy}개 · 올릴 커밋 {status.AheadBy}개"` 대입 하나.

- `RemoteStatusText = $"받을 커밋 {status.BehindBy}개 · 올릴 커밋 {status.AheadBy}개";` → `LastRemoteStatus = status;`
- `RemoteStatusText = null;` 여섯 곳 → `LastRemoteStatus = null;`
- Pull 성공 갈래의 주석 `"받을 커밋 3개"가 방금 다 받은 뒤에도` → `"Pull ↓3"이 방금 다 받은 뒤에도`
- 브랜치 조작 갈래의 주석 `남기면 "브랜치: PROJ-123" 옆에 develop의 앞섬·뒤처짐이 뜬다.` → `남기면 PROJ-123으로 바꾼 뒤에도 Pull·Push 버튼에 develop의 숫자가 붙어 있다.`

Run: `grep -n "RemoteStatusText\|HasRemoteStatus" src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs`
Expected: 출력 없음.

- [ ] **Step 4: XAML에서 옛 표시를 걷고 버튼에 숫자를 붙인다**

`ViewChangesControl.xaml`에서 `RemoteStatusLabel` `TextBlock`(`<TextBlock x:Name="RemoteStatusLabel"`부터 그 요소의 `/>`까지 6줄)을 지운다.

Pull·Push 버튼 두 줄 묶음을 아래로 바꾼다. 폭을 고정하지 않는다 — 숫자가 붙으면 글자가 길어진다.

```xml
                    <Button x:Name="PullButton" Content="{Binding PullButtonText}" Command="{Binding PullCommand}"
                            MinWidth="70" Padding="8,1" Margin="0,0,10,4"
                            ToolTip="원격 저장소의 변경을 로컬 저장소로 가져옵니다. 데이터베이스에는 적용하지 않습니다.&#10;숫자는 마지막으로 '원격 확인'을 누른 시점의 값입니다. 자동으로 갱신되지 않습니다." />
                    <Button x:Name="PushButton" Content="{Binding PushButtonText}" Command="{Binding PushCommand}"
                            MinWidth="70" Padding="8,1" Margin="0,0,10,4"
                            ToolTip="로컬 저장소의 커밋을 원격 저장소에 올립니다.&#10;숫자는 마지막으로 '원격 확인'을 누른 시점의 값입니다. 자동으로 갱신되지 않습니다." />
```

- [ ] **Step 5: 레이아웃 테스트를 옮긴다**

`TopRowLayoutTests.cs`에서 `RemoteStatusLabel_IsHidden_WhenThereIsNoRemoteStatus`와 `RemoteStatusLabel_SitsLeftOfTheBranchLabel_OnTheFirstLine` 두 테스트를 (앞의 `/// <summary>` 주석째) 지운다.

`ViewChangesControlFixtures.cs`의 주석 `// 원격 확인은 수동 버튼으로만 돌므로, RemoteStatusLabel을 채우려면 여기서 직접 눌러야 한다.`를 `// 원격 확인은 수동 버튼으로만 돌므로, Pull·Push 버튼의 숫자를 채우려면 여기서 직접 눌러야 한다.`로 고친다.

같은 파일 `NewConnectedControl` 메서드 뒤(클래스 닫는 괄호 앞)에 도우미 셋을 더한다. `using System.Windows;`를 파일 위 using에 더한다.

```csharp
        /// <summary>폭을 고정하고 높이는 내용이 원하는 만큼 주어 배치한다.</summary>
        public static void LayoutAt(ViewChangesControl control, double width)
        {
            control.Measure(new Size(width, double.PositiveInfinity));
            control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
            control.UpdateLayout();
        }

        public static Point TopLeftOf(ViewChangesControl control, string name)
            => Find<FrameworkElement>(control, name).TranslatePoint(new Point(0, 0), control);

        /// <summary>이름이 틀리면 null 대신 이름을 밝혀 실패한다 - XAML에서 이름을 바꾸고 테스트를 놓친 경우다.</summary>
        public static T Find<T>(ViewChangesControl control, string name) where T : class
            => control.FindName(name) as T
               ?? throw new System.InvalidOperationException($"XAML에서 '{name}'({typeof(T).Name})을 찾지 못했습니다.");
```

새 파일 `tests/DBVC.Vsix.Tests/UI/ChangeListToolbarLayoutTests.cs`:

```csharp
#if NETFRAMEWORK
using System.Windows.Controls;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.UI;
using static DBVC.Vsix.Tests.UI.ViewChangesControlFixtures;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 변경 목록 위 도구 줄(동기화·커밋·체크)의 배치. WPF 레이아웃은 CI가 검증하지 않는 영역이라
    /// 실제 컨트롤을 STA로 배치해 좌표와 값을 직접 본다.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class ChangeListToolbarLayoutTests
    {
        /// <summary>초기화된 Write 대상 - 변경 목록 영역이 보이는 상태다.</summary>
        private static ViewChangesControl NewWriteControl(RemoteStatus? remoteStatus = null)
            => NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                remoteStatus,
                installedVersion: StateTracker.RequiredSchemaVersion);

        [Test]
        public void PullAndPushButtons_ShowTheCounts_AfterCheckingTheRemote()
        {
            var control = NewWriteControl(new RemoteStatus(2, 1));

            LayoutAt(control, 600);

            Assert.That(Find<Button>(control, "PullButton").Content, Is.EqualTo("Pull ↓1"));
            Assert.That(Find<Button>(control, "PushButton").Content, Is.EqualTo("Push ↑2"));
        }
    }
}
#endif
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: 전체 PASS.

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~TopRowLayoutTests|FullyQualifiedName~ChangeListToolbarLayoutTests"`
Expected: 전체 PASS. `TopRowLayoutTests`는 삭제한 둘을 뺀 나머지가 그대로 통과한다.

- [ ] **Step 7: 커밋한다**

```bash
git add src/DBVC.Vsix/ViewModels/ViewChangesViewModel.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/ViewModels/ViewChangesViewModelTests.cs tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs tests/DBVC.Vsix.Tests/UI/ViewChangesControlFixtures.cs tests/DBVC.Vsix.Tests/UI/ChangeListToolbarLayoutTests.cs
git commit -m "feat(vsix): 원격 확인 숫자를 Pull·Push 버튼에 붙인다" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: 드롭다운 도우미와 브랜치 메뉴 버튼

**Files:**
- Create: `src/DBVC.Vsix/UI/DropDownMenu.cs`
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (대상 줄: `BranchLabel` 51~61행, 새 브랜치·브랜치 전환 버튼 72~79행)
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs` (처리기 하나)
- Modify: `tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs` (`BranchLabel_*` 두 테스트)
- Create: `tests/DBVC.Vsix.Tests/UI/DropDownMenuTests.cs`

**Interfaces:**
- Consumes: `ViewChangesControlFixtures.LayoutAt`/`TopLeftOf`/`Find<T>` (Task 2)
- Produces:
  - `public static class DropDownMenu` — `public static ContextMenu? Prepare(Button button)`, `public static void Open(Button button)`
  - 코드비하인드 `private void OnDropDownButtonClick(object sender, RoutedEventArgs e)` — Task 4의 두 버튼도 이 처리기를 쓴다.
  - XAML 이름 `BranchMenuButton`
  - `DropDownMenuTests`의 `[TestCase]` 두 테스트 — Task 4가 케이스를 더한다.

`DBVC.Vsix.csproj`는 SDK를 명시적으로 Import하는 형식이라 새 `.cs`가 자동으로 포함된다(`Compile Include` 목록이 없다). csproj는 건드리지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

새 파일 `tests/DBVC.Vsix.Tests/UI/DropDownMenuTests.cs`:

```csharp
#if NETFRAMEWORK
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.UI;
using DBVC.Vsix.ViewModels;
using static DBVC.Vsix.Tests.UI.ViewChangesControlFixtures;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 도구 줄 드롭다운 메뉴의 배선. 셋 다 "메뉴는 뜨는데 눌러도 아무 일이 없다"로 조용히 깨지는
    /// 종류라, 팝업을 띄우지 않고 준비 단계(DropDownMenu.Prepare)의 결과를 직접 본다.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class DropDownMenuTests
    {
        /// <summary>
        /// ContextMenu는 별도 시각 트리라 DataContext를 상속받지 않는다. Prepare가 넘겨주지 않으면
        /// 항목의 Command 바인딩이 null로 남는다.
        /// </summary>
        [TestCase("BranchMenuButton", 0, nameof(ViewChangesViewModel.SwitchBranchCommand))]
        [TestCase("BranchMenuButton", 1, nameof(ViewChangesViewModel.CreateBranchCommand))]
        public void Prepare_BindsTheMenuItemToTheViewModelCommand(string buttonName, int itemIndex, string commandName)
        {
            var control = NewControl();
            LayoutAt(control, 600);
            var vm = (ViewChangesViewModel)control.DataContext;

            var menu = DropDownMenu.Prepare(Find<Button>(control, buttonName));
            // 바인딩은 DataContext 변경 뒤 DataBind 우선순위에서 갱신될 수 있다. 그때까지 흘려보낸다.
            control.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.That(menu, Is.Not.Null, "버튼에 ContextMenu가 붙어 있어야 합니다");
            Assert.That(menu!.DataContext, Is.SameAs(vm));
            var item = (MenuItem)menu.Items[itemIndex];
            var expected = typeof(ViewChangesViewModel).GetProperty(commandName)!.GetValue(vm);
            Assert.That(item.Command, Is.SameAs(expected));
        }

        /// <summary>
        /// 메뉴는 VS가 칠해 주지 않는다. WPF 기본 전경도 검정이라 기본값과 겹치지 않는 색(Magenta)을
        /// 주어 "셸 브러시를 받았는지"만 가른다 - AuthorToggle 테스트와 같은 방식이다.
        /// </summary>
        [TestCase("BranchMenuButton")]
        public void Prepare_TakesTheMenuColorsFromTheShellTheme(string buttonName)
        {
            var control = NewControl();
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey] = Brushes.Magenta;
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBackgroundKey] = Brushes.Cyan;
            LayoutAt(control, 600);

            var menu = DropDownMenu.Prepare(Find<Button>(control, buttonName))!;

            Assert.That(menu.Foreground, Is.SameAs(Brushes.Magenta));
            Assert.That(menu.Background, Is.SameAs(Brushes.Cyan));
        }

        /// <summary>
        /// 우클릭으로 열리면 Prepare를 거치지 않아 항목이 눌려도 아무 일이 없다. 우클릭 메뉴는
        /// 설계상 쓰지 않으므로(스펙 2.3) 자동 열림을 끈다.
        /// </summary>
        [TestCase("BranchMenuButton")]
        public void DropDownButton_DoesNotOpenOnRightClick(string buttonName)
        {
            var control = NewControl();

            Assert.That(ContextMenuService.GetIsEnabled(Find<Button>(control, buttonName)), Is.False);
        }

        /// <summary>
        /// 배포·감사 클론의 윗줄에도 브랜치 버튼은 보이고 메뉴도 열리지만, 두 항목은 잠겨 있어야 한다
        /// (스펙 2.7). 고정 브랜치를 옮기는 길이 메뉴로 새어 나오면, 병합 영역이 가리키는 목적지와
        /// 저장소가 어긋나 차단 오버레이가 뜨는 상태를 사용자가 스스로 만든다.
        /// </summary>
        [Test]
        public void BranchMenu_ItemsAreDisabled_WhenTheTargetIsADeployClone()
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy);
            LayoutAt(control, 600);

            var button = Find<Button>(control, "BranchMenuButton");
            Assert.That(button.Visibility, Is.EqualTo(Visibility.Visible), "배포 클론에서도 브랜치 이름은 보여야 한다");

            var menu = DropDownMenu.Prepare(button)!;
            control.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.That(menu.Items.Count, Is.EqualTo(2));
            foreach (MenuItem item in menu.Items)
            {
                Assert.That(item.Command, Is.Not.Null, $"'{item.Header}'의 명령 바인딩이 풀리지 않았다");
                Assert.That(item.Command!.CanExecute(null), Is.False, $"'{item.Header}'는 배포 클론에서 잠겨야 한다");
            }
        }
    }
}
#endif
```

`TopRowLayoutTests.cs`에서 `BranchLabel_SitsLeftOfTheVersion_OnTheFirstLine`와 `BranchLabel_IsHidden_WhenThereIsNoBranch`를 아래 둘로 바꾼다(주석째).

```csharp
        /// <summary>
        /// 브랜치 메뉴 버튼은 버전 왼쪽, 같은 첫째 줄에 있어야 한다. DockPanel은 먼저 Dock된 것이 더
        /// 바깥이라 XAML에서 두 요소의 순서를 뒤집으면 브랜치가 버전 오른쪽으로 밀린다 -
        /// 눈으로만 보면 놓치는 종류의 실수라 좌표로 못박는다. 버튼은 테두리만큼 글자선이 달라
        /// 세로는 몇 픽셀의 차이를 허용한다.
        /// </summary>
        [Test]
        public void BranchMenuButton_SitsLeftOfTheVersion_OnTheFirstLine()
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = "feature/x", BlockReason = RepositoryBlockReason.None });

            LayoutAt(control, 600);

            var branch = TopLeftOf(control, "BranchMenuButton");
            var version = TopLeftOf(control, "VersionLabel");

            Assert.That(branch.X, Is.LessThan(version.X), "브랜치가 버전 왼쪽에 와야 한다");
            Assert.That(branch.Y, Is.EqualTo(version.Y).Within(6), "둘은 같은 줄에 있어야 한다");
        }

        /// <summary>브랜치를 알 수 없으면 버튼째 없어야 한다. " ▾"만 남은 버튼은 오해를 준다.</summary>
        [Test]
        public void BranchMenuButton_IsHidden_WhenThereIsNoBranch()
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = null, BlockReason = RepositoryBlockReason.None });

            LayoutAt(control, 600);

            var branch = (FrameworkElement)control.FindName("BranchMenuButton");
            Assert.That(branch, Is.Not.Null, "XAML에 BranchMenuButton이 있어야 한다");
            Assert.That(branch.Visibility, Is.Not.EqualTo(Visibility.Visible));
        }
```

`TopRowLayoutTests`는 자기 `LayoutAt`·`TopLeftOf`를 이미 갖고 있으므로 그대로 쓴다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~DropDownMenuTests|FullyQualifiedName~BranchMenuButton"`
Expected: 컴파일 실패 — `The name 'DropDownMenu' does not exist in the current context`.

- [ ] **Step 3: 도우미를 만든다**

새 파일 `src/DBVC.Vsix/UI/DropDownMenu.cs`:

```csharp
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace DBVC.Vsix.UI
{
    /// <summary>
    /// 버튼에 붙은 ContextMenu를 버튼 아래 드롭다운으로 연다. 도구 줄의 세 메뉴가 같은 함정을
    /// 공유하므로 한 곳에 둔다 - 하나만 고치고 나머지를 놓치면 그 메뉴만 조용히 먹통이 된다.
    /// </summary>
    public static class DropDownMenu
    {
        public static void Open(Button button)
        {
            var menu = Prepare(button);
            if (menu != null) menu.IsOpen = true;
        }

        /// <summary>
        /// 여는 것만 빼고 준비한다. 팝업을 띄우지 않고 배선을 검증하려는 테스트가 이것을 부른다.
        /// </summary>
        public static ContextMenu? Prepare(Button button)
        {
            var menu = button.ContextMenu;
            if (menu == null) return null;

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;

            // ContextMenu는 별도 시각 트리라 DataContext를 상속받지 않는다. 주지 않으면 항목의
            // Command 바인딩이 조용히 실패해 메뉴는 뜨는데 눌러도 아무 일이 없다.
            menu.DataContext = button.DataContext;

            // DynamicResource로 두지 않는다 - 열리기 전의 ContextMenu는 컨트롤의 리소스 트리에 붙어
            // 있지 않아 키가 풀리는 시점을 보장할 수 없다. 열 때마다 버튼에서 다시 찾으므로 테마를
            // 바꿔도 다음에 열 때 따라간다. VS 밖(테스트·디자이너)에서 키가 없으면 WPF 기본값으로 둔다.
            if (button.TryFindResource(VsBrushes.ToolWindowBackgroundKey) is Brush background)
            {
                menu.Background = background;
            }

            if (button.TryFindResource(VsBrushes.ToolWindowTextKey) is Brush foreground)
            {
                menu.Foreground = foreground;
            }

            return menu;
        }
    }
}
```

- [ ] **Step 4: 코드비하인드 처리기를 더한다**

`ViewChangesControl.xaml.cs`의 `OnUnloaded` 메서드 바로 뒤에 더한다.

```csharp
        /// <summary>도구 줄 드롭다운 버튼 셋(브랜치, 새로고침 ▾, 스크립트 ▾)이 함께 쓴다.</summary>
        private void OnDropDownButtonClick(object sender, RoutedEventArgs e)
        {
            DropDownMenu.Open((Button)sender);
        }
```

파일 위 using에 `System.Windows.Controls`와 `System.Windows`가 이미 있다. `RoutedEventArgs`가 모호하다는 오류가 나면 `System.Windows.RoutedEventArgs`로 적는다(같은 파일의 `OnLoaded`가 그렇게 쓴다).

- [ ] **Step 5: 대상 줄 XAML을 바꾼다**

`BranchLabel` `TextBlock`과 그 위 주석(`브랜치는 버전 왼쪽에 붙인다...`)을 아래로 바꾼다. 자리는 그대로 `VersionLabel` 바로 다음이다.

```xml
                <!--
                    브랜치는 버전 왼쪽에 붙인다. DockPanel은 먼저 Dock된 것이 더 바깥이라
                    VersionLabel 다음에 두어야 이 순서가 나온다. 비교 기준이 브랜치 내용이므로
                    이것이 보이지 않으면 사용자가 diff를 오독한다.

                    이름 자체가 메뉴 버튼이다. 새 브랜치·전환을 따로 버튼으로 두었을 때는 대상 표시
                    바로 뒤에 붙어 "Windows 인증"을 가렸다. 배포·감사 클론에서도 메뉴는 열린다 - 두
                    항목이 잠긴 채 보이는 것이 "이 클론은 브랜치가 고정되어 있다"를 말한다.
                    메뉴를 여는 버튼에는 명령이 없어 CanExecute로 잠기지 않으므로 작업 중에는
                    IsNotBusy로 잠근다.
                -->
                <Button x:Name="BranchMenuButton" DockPanel.Dock="Right"
                        Content="{Binding CurrentBranch}" ContentStringFormat="{}{0} ▾"
                        VerticalAlignment="Top" Margin="8,0,0,4" Padding="8,1"
                        Visibility="{Binding HasCurrentBranch, Converter={StaticResource BoolToVis}}"
                        IsEnabled="{Binding IsNotBusy}"
                        ContextMenuService.IsEnabled="False"
                        Click="OnDropDownButtonClick"
                        ToolTip="저장소가 지금 가리키는 브랜치입니다. 커밋은 이 브랜치에 담깁니다.&#10;눌러서 다른 브랜치로 전환하거나 새 브랜치를 만듭니다.">
                    <Button.ContextMenu>
                        <ContextMenu>
                            <MenuItem Header="브랜치 전환..." Command="{Binding SwitchBranchCommand}"
                                      ToolTipService.ShowOnDisabled="True"
                                      ToolTip="다른 브랜치로 갈아탑니다.&#10;커밋되지 않은 변경이 있으면 갈아타지 않고 무엇이 남았는지 알려 줍니다.&#10;배포·감사 클론은 브랜치가 고정되어 있어 잠깁니다."/>
                            <MenuItem Header="새 브랜치..." Command="{Binding CreateBranchCommand}"
                                      ToolTipService.ShowOnDisabled="True"
                                      ToolTip="지금 브랜치에서 새 브랜치를 만들고 갈아탑니다. 작업 중인 파일은 그대로 남습니다.&#10;배포·감사 클론은 브랜치가 고정되어 있어 잠깁니다."/>
                        </ContextMenu>
                    </Button.ContextMenu>
                </Button>
```

안쪽 `WrapPanel`에서 `새 브랜치`·`브랜치 전환` 두 버튼과 그 위 주석(`고정 브랜치 클론(배포용)에서는 CanExecute가...`)을 지운다. 남는 것은 `ConnectButton`과 `TargetLabel`뿐이다.

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~DropDownMenuTests|FullyQualifiedName~TopRowLayoutTests"`
Expected: 전체 PASS. `VersionLabel_StaysOnTheFirstLine_WhenTheTopRowWraps`와 `BlockOverlay_*` 셋도 그대로 통과해야 한다.

`Prepare_BindsTheMenuItemToTheViewModelCommand`가 `item.Command`이 null이라 실패하면 원인을 둘로 가른다. 테스트에 `Assert.That(item.DataContext, Is.SameAs(vm));`를 잠시 더해 돌린다.

- `item.DataContext`가 `vm`이 아니다 → 메뉴 항목까지 상속이 끊긴 **제품 결함**이다. `DropDownMenu.Prepare`의 `menu.DataContext = button.DataContext;` 다음에 `foreach (var item in menu.Items.OfType<FrameworkElement>()) item.DataContext = button.DataContext;`를 더하고(`using System.Linq;`, `using System.Windows;`), 왜 필요한지 주석을 단다.
- `item.DataContext`는 `vm`인데 `Command`만 null이다 → 바인딩 갱신이 아직 돌지 않은 **테스트 타이밍**이다. 테스트의 `DispatcherPriority.ContextIdle`을 `DispatcherPriority.ApplicationIdle`로 바꾼다.

확인이 끝나면 임시 단언을 지운다.

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: 전체 PASS.

- [ ] **Step 7: 커밋한다**

```bash
git add src/DBVC.Vsix/UI/DropDownMenu.cs src/DBVC.Vsix/UI/ViewChangesControl.xaml src/DBVC.Vsix/UI/ViewChangesControl.xaml.cs tests/DBVC.Vsix.Tests/UI/DropDownMenuTests.cs tests/DBVC.Vsix.Tests/UI/TopRowLayoutTests.cs
git commit -m "feat(vsix): 브랜치 이름을 새 브랜치·전환 메뉴 버튼으로 만든다" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: 변경 목록 위 도구 줄을 세 줄로 나눈다

**Files:**
- Modify: `src/DBVC.Vsix/UI/ViewChangesControl.xaml` (`ChangeListGrid` 안쪽 `Grid`: 행 정의, 도구 줄 `WrapPanel` 전체, `ListView`·`GridSplitter`·`TabControl`의 `Grid.Row`)
- Modify: `tests/DBVC.Vsix.Tests/UI/ChangeListToolbarLayoutTests.cs`
- Modify: `tests/DBVC.Vsix.Tests/UI/DropDownMenuTests.cs` (`[TestCase]` 추가)

**Interfaces:**
- Consumes: `CheckedCount` (Task 1), `PullButtonText`/`PushButtonText` (Task 2), `OnDropDownButtonClick`·`DropDownMenu.Prepare` (Task 3), fixtures 도우미 (Task 2)
- Produces: XAML 이름 `RefreshButton`, `RefreshMenuButton`, `PullButton`, `PushButton`, `CheckRemoteButton`, `CommitMessageBox`, `CommitMessagePlaceholder`, `GenerateMessageButton`, `CommitButton`, `CheckedCountLabel`, `DiscardButton`, `IgnoreButton`, `ScriptMenuButton`, `AuthorToggle`(기존 이름 유지)

**동기화 줄 정렬.** 스펙 3.1은 좌우 정렬을 시도하되 `WrapPanel`에서 성립하지 않으면 왼쪽 정렬로 물러서라고 했다. `WrapPanel`은 줄 안에서 자식을 오른쪽에 붙이는 방법이 없으므로 **처음부터 왼쪽 정렬**로 두고, 두 무리 사이를 여백 16으로 가른다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`ChangeListToolbarLayoutTests.cs`의 클래스 안, 기존 테스트 뒤에 더한다. 파일 위 using에 `System.Windows`를 더한다.

```csharp
        /// <summary>넓은 창에서는 추출 무리와 동기화 무리가 한 줄에 있어야 한다. 아니면 아래 좁은 창 테스트의 전제가 없다.</summary>
        [Test]
        public void SyncGroup_SharesTheLineWithRefresh_WhenTheWindowIsWide()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            Assert.That(TopLeftOf(control, "PullButton").Y,
                Is.EqualTo(TopLeftOf(control, "RefreshButton").Y).Within(1));
        }

        /// <summary>
        /// 좁은 창에서는 동기화 무리가 통째로 내려가야 한다. 버튼을 한 WrapPanel에 흘려 두면 Pull만
        /// 윗줄에 남고 Push가 아랫줄로 떨어져, 함께 읽어야 할 숫자가 갈라진다.
        /// </summary>
        [Test]
        public void SyncGroup_WrapsAsOneUnit_WhenTheWindowIsNarrow()
        {
            var control = NewWriteControl(new RemoteStatus(12, 34));

            LayoutAt(control, 300);

            var refresh = TopLeftOf(control, "RefreshButton").Y;
            var pull = TopLeftOf(control, "PullButton").Y;
            Assert.That(pull, Is.GreaterThan(refresh + 6),
                "300px에서는 동기화 무리가 다음 줄로 내려가야 한다. 아니면 이 테스트의 전제가 틀렸다.");
            Assert.That(TopLeftOf(control, "PushButton").Y, Is.EqualTo(pull).Within(1));
            Assert.That(TopLeftOf(control, "CheckRemoteButton").Y, Is.EqualTo(pull).Within(1));
        }

        /// <summary>메시지 칸은 남는 폭을 모두 써야 한다. 고정 240이면 넓은 창에서 칸만 좁게 남는다.</summary>
        [Test]
        public void CommitMessageBox_Widens_WhenTheWindowWidens()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);
            var narrow = Find<FrameworkElement>(control, "CommitMessageBox").ActualWidth;
            LayoutAt(control, 900);
            var wide = Find<FrameworkElement>(control, "CommitMessageBox").ActualWidth;

            Assert.That(wide, Is.GreaterThan(narrow + 200));
        }

        /// <summary>안내 글자는 칸이 비었을 때만 보여야 한다. 적은 글자 위에 겹치면 읽히지 않는다.</summary>
        [Test]
        public void CommitMessagePlaceholder_IsVisibleOnlyWhileTheMessageIsEmpty()
        {
            var control = NewWriteControl();
            var vm = (DBVC.Vsix.ViewModels.ViewChangesViewModel)control.DataContext;

            LayoutAt(control, 600);
            Assert.That(Find<FrameworkElement>(control, "CommitMessagePlaceholder").Visibility,
                Is.EqualTo(Visibility.Visible), "메시지가 비어 있으면 안내가 보여야 한다");

            vm.CommitMessage = "기능 추가";
            LayoutAt(control, 600);
            Assert.That(Find<FrameworkElement>(control, "CommitMessagePlaceholder").Visibility,
                Is.Not.EqualTo(Visibility.Visible), "메시지가 있으면 안내가 사라져야 한다");

            vm.CommitMessage = "";
            LayoutAt(control, 600);
            Assert.That(Find<FrameworkElement>(control, "CommitMessagePlaceholder").Visibility,
                Is.EqualTo(Visibility.Visible), "지우면 다시 보여야 한다");
        }

        [Test]
        public void CheckedCountLabel_ShowsTheCount()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            Assert.That(Find<TextBlock>(control, "CheckedCountLabel").Text, Is.EqualTo("체크한 항목 0개"));
        }

        /// <summary>
        /// 체크 작업 줄은 목록 바로 위에 있어야 한다 - 체크한 항목에 작용한다는 것을 자리가 말한다.
        /// 커밋 줄보다 아래, 목록보다 위.
        /// </summary>
        [Test]
        public void CheckedItemRow_SitsBetweenTheCommitRowAndTheList()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            var commit = TopLeftOf(control, "CommitButton").Y;
            var discard = TopLeftOf(control, "DiscardButton").Y;
            var list = TopLeftOf(control, "ChangeList").Y;
            Assert.That(discard, Is.GreaterThan(commit + 6));
            Assert.That(list, Is.GreaterThan(discard + 6));
        }

        /// <summary>
        /// 체크 줄의 버튼은 CanExecute가 잠근다. 화면에 IsEnabled를 따로 걸면 같은 판정이 두 곳에
        /// 생긴다(스펙 2.4) - 변경이 없으면 잠겨 있어야 하고, 그것을 명령이 해냈는지만 본다.
        /// </summary>
        [Test]
        public void DiscardAndIgnoreButtons_AreDisabled_WhenNothingIsChecked()
        {
            var control = NewWriteControl();

            LayoutAt(control, 600);

            Assert.That(Find<Button>(control, "DiscardButton").IsEnabled, Is.False);
            Assert.That(Find<Button>(control, "IgnoreButton").IsEnabled, Is.False);
        }

        /// <summary>
        /// 새 도구 줄은 변경 목록 영역(ChangeListGrid) 안에 있어야 한다. 윗줄로 끌어올리거나 바깥 Grid에
        /// 두면 배포·감사 클론에서 병합 영역 위에 Pull·Push·Commit이 새어 나온다 - 배포 클론은 Push가
        /// 금지이고, 병합은 자기가 만든 커밋 하나만 올리도록 짜여 있다(스펙 2.7).
        /// </summary>
        [TestCase("RefreshButton")]
        [TestCase("PullButton")]
        [TestCase("CommitButton")]
        [TestCase("DiscardButton")]
        [TestCase("ScriptMenuButton")]
        public void ChangeListToolbar_StaysInsideTheChangeList_WhenTheTargetIsADeployClone(string name)
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy);

            LayoutAt(control, 600);

            var changeList = Find<FrameworkElement>(control, "ChangeListGrid");
            var deployment = Find<FrameworkElement>(control, "DeploymentPanelGrid");
            Assert.That(deployment.Visibility, Is.EqualTo(Visibility.Visible), "전제: 배포 클론은 배포 패널을 본다");
            Assert.That(changeList.Visibility, Is.Not.EqualTo(Visibility.Visible), "전제: 변경 목록 영역은 숨는다");
            Assert.That(IsDescendantOf(Find<DependencyObject>(control, name), changeList), Is.True,
                $"'{name}'이 변경 목록 영역 밖에 있어 배포 화면에 보인다");
        }

        private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
        {
            for (var current = node; current != null; current = LogicalTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, ancestor)) return true;
            }

            return false;
        }
```

위 테스트가 쓰는 `MappingMode`를 위해 파일 위 using에 `DBVC.Core.Models`가 있는지 확인한다(Task 2에서 이미 넣었다).

`DropDownMenuTests.cs`의 세 테스트에 케이스를 더한다.

`Prepare_BindsTheMenuItemToTheViewModelCommand` 위:
```csharp
        [TestCase("RefreshMenuButton", 0, nameof(ViewChangesViewModel.RefreshAllCommand))]
        [TestCase("ScriptMenuButton", 0, nameof(ViewChangesViewModel.GenerateDeploymentScriptCommand))]
        [TestCase("ScriptMenuButton", 1, nameof(ViewChangesViewModel.GenerateRollbackScriptCommand))]
```

`Prepare_TakesTheMenuColorsFromTheShellTheme` 위와 `DropDownButton_DoesNotOpenOnRightClick` 위에 각각:
```csharp
        [TestCase("RefreshMenuButton")]
        [TestCase("ScriptMenuButton")]
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ChangeListToolbarLayoutTests|FullyQualifiedName~DropDownMenuTests"`
Expected: 새 테스트들이 FAIL — `XAML에서 'RefreshButton'(FrameworkElement)을 찾지 못했습니다.` 등. Task 2의 `PullAndPushButtons_ShowTheCounts_AfterCheckingTheRemote`와 Task 3의 `BranchMenuButton` 케이스는 PASS.

- [ ] **Step 3: 행 정의와 목록 행 번호를 바꾼다**

`ChangeListGrid` 안쪽 `Grid`의 `RowDefinitions`를 바꾼다.

```xml
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                    <RowDefinition Height="5" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>
```

- `<!-- Middle Area -->` 아래 `ListView`: `Grid.Row="1"` → `Grid.Row="3"`, 그리고 `x:Name="ChangeList"`를 더한다.
- 그 아래 `GridSplitter`: `Grid.Row="2"` → `Grid.Row="4"`.
- `<!-- Bottom Area ... -->` 아래 `TabControl`: `Grid.Row="3"` → `Grid.Row="5"`.

- [ ] **Step 4: 도구 줄을 세 줄로 바꾼다**

`<!-- Top Area. 버튼이 늘어 좁게 도킹하면 잘리므로 WrapPanel로 줄바꿈되게 한다. -->` 주석부터 그 `WrapPanel`의 닫는 태그 `</WrapPanel>`까지를 통째로 아래로 바꾼다.

```xml
                <!--
                    도구 줄은 무리 단위로 줄바꿈한다. 버튼을 한 WrapPanel에 흘려 두었을 때는 좁게
                    도킹하면 Pull만 윗줄에 남고 Push가 아랫줄로 떨어져, 함께 읽어야 할 숫자가 갈라졌다.

                    컨테이너에 TextElement.Foreground를 걸지 않는다. 이 영역은 위쪽 StackPanel 바깥이라
                    상속을 받지 못하는데, 여기서 걸면 커밋 메시지 TextBox까지 흘러 들어가 어두운 테마에서
                    흰 바탕에 흰 글씨가 된다. 글자를 가진 TextBlock·CheckBox에만 직접 준다.
                -->
                <WrapPanel Grid.Row="0" Orientation="Horizontal" Margin="5,5,5,0">
                    <StackPanel Orientation="Horizontal" Margin="0,0,16,4">
                        <Button x:Name="RefreshButton" Content="새로고침" Command="{Binding RefreshCommand}"
                                MinWidth="80" Padding="8,1"
                                ToolTip="DDL 로그가 기록한 변경만 다시 추출합니다. 빠릅니다."/>
                        <!--
                            전체 다시 추출은 새로고침의 느린 변형이고 DB 전체를 긁는다. 가끔 쓰므로 접는다.
                            메뉴를 여는 버튼에는 명령이 없어 CanExecute로 잠기지 않으므로 작업 중에는
                            IsNotBusy로 잠근다 - 열린 메뉴가 작업 종료 뒤에 남는 경로를 만들지 않는다.
                        -->
                        <Button x:Name="RefreshMenuButton" Content="▾" Width="22" Margin="1,0,0,0"
                                IsEnabled="{Binding IsNotBusy}"
                                ContextMenuService.IsEnabled="False"
                                Click="OnDropDownButtonClick"
                                ToolTip="다른 추출 방법">
                            <Button.ContextMenu>
                                <ContextMenu>
                                    <MenuItem Header="전체 다시 추출 (느림)" Command="{Binding RefreshAllCommand}"
                                              ToolTipService.ShowOnDisabled="True"
                                              ToolTip="데이터베이스의 모든 객체를 다시 추출합니다. 느리지만, DDL 트리거가 없던 동안의 변경처럼 변경 로그가 모르는 차이를 되찾는 유일한 방법입니다."/>
                                </ContextMenu>
                            </Button.ContextMenu>
                        </Button>
                    </StackPanel>

                    <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                        <Button x:Name="PullButton" Content="{Binding PullButtonText}" Command="{Binding PullCommand}"
                                MinWidth="70" Padding="8,1" Margin="0,0,6,0"
                                ToolTip="원격 저장소의 변경을 로컬 저장소로 가져옵니다. 데이터베이스에는 적용하지 않습니다.&#10;숫자는 마지막으로 '원격 확인'을 누른 시점의 값입니다. 자동으로 갱신되지 않습니다." />
                        <Button x:Name="PushButton" Content="{Binding PushButtonText}" Command="{Binding PushCommand}"
                                MinWidth="70" Padding="8,1" Margin="0,0,6,0"
                                ToolTip="로컬 저장소의 커밋을 원격 저장소에 올립니다.&#10;숫자는 마지막으로 '원격 확인'을 누른 시점의 값입니다. 자동으로 갱신되지 않습니다." />
                        <Button x:Name="CheckRemoteButton" Content="원격 확인" Command="{Binding CheckRemoteCommand}"
                                MinWidth="80" Padding="8,1"
                                ToolTip="원격을 받아 받을 커밋과 올릴 커밋의 수를 셉니다. 작업 트리는 건드리지 않습니다.&#10;누를 때만 네트워크를 씁니다 - 자동으로 갱신되지 않습니다."/>
                    </StackPanel>
                </WrapPanel>

                <!--
                    메시지 칸은 남는 폭을 모두 쓴다. 폭 240 고정의 이름 없는 칸이었을 때는 무엇을 적는
                    곳인지 화면이 말하지 않았다. TextBox에는 placeholder가 없어 같은 셀에 TextBlock을
                    겹친다 - 클릭이 칸으로 가도록 IsHitTestVisible을 끈다.
                -->
                <DockPanel Grid.Row="1" Margin="5,0,5,4" LastChildFill="True">
                    <Button x:Name="CommitButton" DockPanel.Dock="Right" Content="Commit" Command="{Binding CommitCommand}"
                            MinWidth="70" Padding="8,1" Margin="6,0,0,0" />
                    <Button x:Name="GenerateMessageButton" DockPanel.Dock="Right" Content="AI 생성"
                            Command="{Binding GenerateCommitMessageCommand}"
                            MinWidth="70" Padding="8,1" Margin="6,0,0,0"
                            ToolTip="선택한 변경의 내용을 AI에게 보내 커밋 메시지 초안을 만듭니다.&#10;커밋하지는 않습니다 - 내용을 확인하고 고친 뒤 Commit을 누르세요." />
                    <Grid>
                        <TextBox x:Name="CommitMessageBox"
                                 Text="{Binding CommitMessage, UpdateSourceTrigger=PropertyChanged}"
                                 IsEnabled="{Binding IsMapped}"
                                 VerticalContentAlignment="Center"/>
                        <TextBlock x:Name="CommitMessagePlaceholder" Text="커밋 메시지"
                                   Margin="5,0,0,0" VerticalAlignment="Center" IsHitTestVisible="False"
                                   Foreground="{DynamicResource {x:Static vsshell:VsBrushes.GrayTextKey}}">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Text, ElementName=CommitMessageBox}" Value="">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                        <DataTrigger Binding="{Binding Text, ElementName=CommitMessageBox}" Value="{x:Null}">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                    </Grid>
                </DockPanel>

                <!--
                    체크한 항목에 작용하는 것만 모은다. 목록 바로 위에 두어 적용 대상을 자리가 말하게
                    한다. 체크가 없어도 줄은 남는다 - 체크할 때마다 목록이 밀리지 않고, 처음 쓰는 사람도
                    기능이 있다는 것을 안다(스펙 2.4). 잠금은 명령의 CanExecute가 맡는다.

                    우클릭 메뉴로 옮기지 않는다. 우클릭은 클릭한 행을 가리키는데 이 명령들은 체크한
                    항목에 작용하므로, 화면이 무엇이 되돌아가는지 거짓말을 하게 된다(스펙 2.3).
                -->
                <WrapPanel Grid.Row="2" Orientation="Horizontal" Margin="5,0,5,4">
                    <TextBlock x:Name="CheckedCountLabel"
                               Text="{Binding CheckedCount, StringFormat='체크한 항목 {0}개'}"
                               VerticalAlignment="Center" Margin="0,0,10,0"
                               Foreground="{DynamicResource {x:Static vsshell:VsBrushes.ToolWindowTextKey}}"/>
                    <Button x:Name="DiscardButton" Content="되돌리기" Command="{Binding DiscardCommand}"
                            MinWidth="70" Padding="8,1" Margin="0,0,6,0"
                            ToolTip="선택한 파일을 저장소의 마지막 커밋 내용으로 되돌립니다. 데이터베이스의 변경은 남아 다음 새로고침에서 다시 추출됩니다." />
                    <Button x:Name="IgnoreButton" Content="무시" Command="{Binding IgnoreCommand}"
                            MinWidth="70" Padding="8,1" Margin="0,0,6,0"
                            ToolTip="선택한 파일을 되돌리고 변경 로그에서 닫습니다. 이 데이터베이스를 함께 쓰는 모두에게 적용됩니다." />
                    <!--
                        배포·롤백 스크립트는 같은 산출물의 두 방향이라 한 메뉴로 접는다. 메뉴를 여는
                        버튼은 체크가 없어도 열린다 - 두 항목이 잠긴 채 보인다.
                    -->
                    <Button x:Name="ScriptMenuButton" Content="스크립트 ▾"
                            MinWidth="80" Padding="8,1"
                            IsEnabled="{Binding IsNotBusy}"
                            ContextMenuService.IsEnabled="False"
                            Click="OnDropDownButtonClick"
                            ToolTip="체크한 객체로 배포·롤백 스크립트를 만듭니다.">
                        <Button.ContextMenu>
                            <ContextMenu>
                                <MenuItem Header="배포 스크립트..." Command="{Binding GenerateDeploymentScriptCommand}"
                                          ToolTipService.ShowOnDisabled="True"
                                          ToolTip="선택한 객체의 현재 DDL을 단일 .sql 파일로 병합합니다." />
                                <MenuItem Header="롤백 스크립트..." Command="{Binding GenerateRollbackScriptCommand}"
                                          ToolTipService.ShowOnDisabled="True"
                                          ToolTip="선택한 객체가 마지막으로 커밋되기 직전 코드를 단일 .sql 파일로 병합합니다." />
                            </ContextMenu>
                        </Button.ContextMenu>
                    </Button>
                    <!--
                        목록을 거르는 옵션이라 이 줄 맨 뒤에 둔다. 오른쪽 끝에 붙이지 않는다 - DockPanel은
                        줄바꿈하지 않아 좁은 창에서 버튼과 겹친다.

                        작업 중에는 잠근다. 체크박스에는 CanExecute가 없어 버튼과 달리 스스로 막히지
                        않는데, 여기서 새로고침이 겹쳐 돌면 서로의 결과를 덮어쓴다.

                        Foreground를 직접 준다. 맨 CheckBox는 WPF 기본값(검정)이 남아, 어두운 테마에서
                        어두운 바탕에 검은 글씨로 사라졌다.
                    -->
                    <CheckBox x:Name="AuthorToggle" Content="다른 사람 변경도 보기"
                              IsChecked="{Binding ShowAllAuthors, Mode=TwoWay}"
                              IsEnabled="{Binding IsNotBusy}"
                              Foreground="{DynamicResource {x:Static vsshell:VsBrushes.ToolWindowTextKey}}"
                              ToolTip="공용 계정이라 사람은 접속 PC로 구분합니다. 평소에는 자기 변경만 보이는 편이 안전합니다."
                              Margin="16,0,0,0" VerticalAlignment="Center" />
                </WrapPanel>
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/DBVC.Vsix.Tests -f net48 --filter "FullyQualifiedName~ChangeListToolbarLayoutTests|FullyQualifiedName~DropDownMenuTests|FullyQualifiedName~TopRowLayoutTests|FullyQualifiedName~DeploymentPanelLayoutTests|FullyQualifiedName~HistoryLayoutTests|FullyQualifiedName~DeploymentViewModelMergeTests"`
Expected: 전체 PASS. `AuthorToggle_TakesItsForegroundFromTheShellTheme`와 `BlockOverlay_*`가 그대로 통과해야 한다.

`SyncGroup_WrapsAsOneUnit_WhenTheWindowIsNarrow`의 전제 단언("300px에서는 동기화 무리가 다음 줄로 내려가야 한다")이 실패하면 버튼 실측 폭이 예상보다 좁은 것이다. 폭 300을 250으로 낮춰 다시 돌린다. 동기화 무리 자체가 그 폭보다 넓어 무리 안에서 잘린다면(`PushButton` Y가 `PullButton`과 같지만 X가 컨트롤 폭을 넘는다) 전제가 성립하는 가장 큰 폭을 찾아 쓰고, 그 값을 테스트 주석에 적는다.

Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Expected: 전체 PASS.

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/UI/ViewChangesControl.xaml tests/DBVC.Vsix.Tests/UI/ChangeListToolbarLayoutTests.cs tests/DBVC.Vsix.Tests/UI/DropDownMenuTests.cs
git commit -m "feat(vsix): 변경 목록 도구 줄을 동기화·커밋·체크한 항목 세 줄로 묶는다" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: 문서·버전·전체 검증

**Files:**
- Modify: `src/DBVC.Vsix/source.extension.vsixmanifest`
- Modify: `README.md`
- Modify: `docs/user-guide.html`
- Modify: `docs/setup-checklist.md`

행 번호는 적지 않는다. 0.8.0 병합이 README·체크리스트·가이드에 절을 더해 번호가 계속 밀리므로, 아래 인용한 문구로 찾는다(`grep -n "<문구 일부>" <파일>`). 인용한 문구가 없으면 멈추고 보고한다.

- [ ] **Step 1: 매니페스트 버전을 올린다**

Run: `grep -n "Identity Id" src/DBVC.Vsix/source.extension.vsixmanifest`
Expected: `Version="0.8.0"`. 다른 값이면 멈추고 보고한다.

`Version="0.8.0"`을 `Version="0.9.0"`으로 바꾼다. `DbvcVersionTests`는 매니페스트를 읽어 비교하므로 테스트는 고치지 않는다. 병합 문서(`2026-09-17-dbvc-merge-in-deploy-clone*`)의 버전은 건드리지 않는다.

- [ ] **Step 2: README를 고친다**

"배포/롤백 스크립트 생성" 머리 항목(`- **배포/롤백 스크립트 생성:**`) 끝에 한 문장을 더한다:
```
변경 목록 바로 위 줄의 **스크립트 ▾** 메뉴에 있으며, 체크한 객체를 재료로 씁니다.
```

"원격 확인" 머리 항목의
```
`받을 커밋 n개 · 올릴 커밋 n개` 를 상단에 띄웁니다.
```
를
```
받을 커밋과 올릴 커밋의 수를 **Pull ↓n**·**Push ↑n** 처럼 두 버튼에 붙입니다. 숫자가 없으면 아직 확인하지 않은 것이고, `↓0` 은 확인했고 받을 것이 없다는 뜻입니다. Pull·Push·커밋·브랜치 전환 뒤에는 숫자가 사라집니다 — 낡은 숫자를 최신인 척 두지 않기 위해서입니다.
```
로 바꾼다.

"현재 브랜치" 항목의 `도구 창 위쪽에 저장소의 현재 브랜치가 표시됩니다.`를 `도구 창 위쪽 버전 왼쪽에 저장소의 현재 브랜치가 **develop ▾** 처럼 버튼으로 표시됩니다.`로 바꾼다.

"브랜치 만들기·전환(0.6.0)" 항목의 `**새 브랜치**는` 앞에 `브랜치 이름 버튼을 누르면 나오는 메뉴에 있습니다.`를 더하고, 그 항목 끝의
```
두
  버튼 모두 배포·감사 클론에서는 고정 브랜치가 필수라 처음부터 비활성화됩니다.
```
를
```
두
  메뉴 항목 모두 배포·감사 클론에서는 고정 브랜치가 필수라 잠긴 채 보입니다.
```
로 바꾼다.

"전체 다시 추출" 항목의 `이때는 **전체 다시 추출** 을 누르세요.`를 `이때는 **새로고침** 옆 **▾** 메뉴의 **전체 다시 추출** 을 누르세요.`로 바꾼다.

`## 주요 기능` 머리 목록의 마지막 항목 뒤(빈 줄과 `### 기능 커버리지` 앞)에 한 줄을 더한다:
```
- **도구 줄 정리 (0.9.0):** 개발 클론 화면의 버튼을 무리별로 묶었습니다. 첫 줄은 새로고침과 원격 동기화(Pull·Push·원격 확인), 둘째 줄은 커밋 메시지와 Commit, 셋째 줄은 **체크한 항목** 에 작용하는 되돌리기·무시·스크립트입니다. 가끔 쓰는 전체 다시 추출, 배포/롤백 스크립트, 새 브랜치/브랜치 전환은 각각 **새로고침 ▾**, **스크립트 ▾**, 브랜치 이름 메뉴로 옮겼습니다. 배포·감사 클론의 병합·차이 검사 화면은 그대로입니다.
```

Run: `grep -n "받을 커밋 n개\|상단에 띄웁니다\|처음부터 비활성화됩니다" README.md`
Expected: 출력 없음.

- [ ] **Step 3: 사용 설명서(`docs/user-guide.html`)를 고친다**

가이드는 사용자가 직접 읽는 화면 설명이라, 버튼 자리가 바뀌면 그림과 문장이 함께 틀린다.

**머리 버전.** `<p class="kicker">사용 설명서 &nbsp;/&nbsp; 개발자 · DBA &nbsp;/&nbsp; DBVC 0.7.2`의 `0.7.2`를 `0.9.0`으로 바꾼다. (0.8.0 병합 때 올리지 않은 채 남았다.) `v0.7.2`처럼 릴리스 태그를 예로 드는 다른 문장은 건드리지 않는다.

**도구 창 그림.** `aria-label="DBVC 도구 창의 구조`로 시작하는 `<svg>`를 고친다. 동작 버튼 줄이 두 줄(52)에서 세 줄(82)이 되어 그 아래 전부가 30 내려간다.

1. `<svg viewBox="0 0 900 470"`을 `<svg viewBox="0 0 900 500"`으로, 창 `<rect class="box" x="8" y="8" width="600" height="454" rx="4"></rect>`의 `height="454"`를 `height="484"`로 바꾼다.

2. 대상 줄의 아래 두 줄을
```html
          <text class="t-mono" x="592" y="26" text-anchor="end">브랜치: develop</text>
          <text class="t-mono" x="592" y="42" text-anchor="end">DBVC 0.7.2</text>
```
아래로 바꾼다.
```html
          <rect class="btn" x="462" y="17" width="66" height="19" rx="2"></rect>
          <text class="t-mono" x="495" y="31" text-anchor="middle">develop ▾</text>
          <text class="t-mono" x="592" y="31" text-anchor="end">DBVC 0.9.0</text>
```

3. `<!-- 동작 버튼 줄 -->`부터 그 `</g>`까지를 아래로 바꾼다.
```html
          <!-- 동작 버튼 줄: 동기화 · 커밋 · 체크한 항목 -->
          <rect class="band" x="16" y="94" width="584" height="82" rx="2"></rect>
          <g class="t-edge">
            <rect class="btn" x="26"  y="100" width="52" height="19" rx="2"></rect><text x="52"  y="113" text-anchor="middle">새로고침</text>
            <rect class="btn" x="80"  y="100" width="14" height="19" rx="2"></rect><text x="87"  y="113" text-anchor="middle">▾</text>
            <rect class="btn" x="110" y="100" width="46" height="19" rx="2"></rect><text x="133" y="113" text-anchor="middle">Pull ↓0</text>
            <rect class="btn" x="160" y="100" width="46" height="19" rx="2"></rect><text x="183" y="113" text-anchor="middle">Push ↑2</text>
            <rect class="btn" x="210" y="100" width="52" height="19" rx="2"></rect><text x="236" y="113" text-anchor="middle">원격 확인</text>

            <rect class="btn" x="26"  y="126" width="452" height="19" rx="2"></rect><text x="34" y="139">커밋 메시지</text>
            <rect class="btn" x="484" y="126" width="44" height="19" rx="2"></rect><text x="506" y="139" text-anchor="middle">AI 생성</text>
            <rect class="btn" x="534" y="126" width="56" height="19" rx="2"></rect><text x="562" y="139" text-anchor="middle">Commit</text>

            <text x="26" y="165">체크한 항목 2개</text>
            <rect class="btn" x="106" y="152" width="50" height="19" rx="2"></rect><text x="131" y="165" text-anchor="middle">되돌리기</text>
            <rect class="btn" x="160" y="152" width="34" height="19" rx="2"></rect><text x="177" y="165" text-anchor="middle">무시</text>
            <rect class="btn" x="198" y="152" width="58" height="19" rx="2"></rect><text x="227" y="165" text-anchor="middle">스크립트 ▾</text>
            <rect class="btn" x="272" y="156" width="10" height="10" rx="1"></rect>
            <text x="288" y="165">다른 사람 변경도 보기</text>
          </g>
```

4. `<!-- 변경 목록 -->` 주석 바로 앞에 `<g transform="translate(0,30)">`를 열고, `<!-- 탭 -->` 절의 마지막 `</g>`(Old/New 코드 줄을 담은 `<g class="t-mono">`의 닫는 태그) 바로 뒤에 `</g>`로 닫는다. 변경 목록과 탭이 통째로 30 내려간다.

5. `<!-- 설명 -->` 묶음에서 동작 버튼 줄 설명(`M 608 118 L 660 118`로 시작하는 넷)을 아래로 바꾼다.
```html
            <path class="flow-dash" d="M 608 135 L 660 135"></path>
            <text class="t-note" x="668" y="116">위에서부터 원격과 주고받기,</text>
            <text class="t-note" x="668" y="131">커밋하기,</text>
            <text class="t-note" x="668" y="146">체크한 것에 할 일.</text>
            <text class="t-note" x="668" y="161">가끔 쓰는 것은 ▾ 메뉴 안에.</text>
```
   변경 목록 설명(`M 608 210 L 660 210`과 그 세 줄)과 탭 설명(`M 608 385 L 660 385`와 그 세 줄)은 각각 `<g transform="translate(0,30)">` … `</g>`로 감싼다.

6. 대상 줄 설명의 `대상과 브랜치와 버전.`은 그대로 둔다.

7. 그림 바로 아래 `<figcaption>`의
```html
        <span class="b">차이 검사</span>·<span class="b">배포 스크립트 저장...</span> 둘만 있는
        별도 패널이 뜹니다(<a href="#dba">3장</a>).
```
를
```html
        병합 영역(<span class="b">병합할 브랜치 확인</span>·<span class="b">병합</span>)과
        <span class="b">차이 검사</span>·<span class="b">배포 스크립트 저장...</span>이 있는
        별도 패널이 뜹니다(<a href="#dba">3장</a>).
```
로 바꾼다. 0.8.0에서 병합 영역이 들어왔는데 캡션이 따라오지 않았다 — 이 그림을 고치는 김에 바로잡는다.

**문장.**

- `전환은 <span class="b">새 브랜치</span>·<span class="b">브랜치 전환</span> 버튼으로` → `전환은 오른쪽 위 브랜치 이름 버튼(<code>develop ▾</code>)의 메뉴로`
- 같은 문단의 `<code>브랜치: …</code>에서 봅니다.` → `그 버튼의 이름에서 봅니다.`
- `원격을 받아 <code>받을 커밋 n개 · 올릴 커밋 n개</code>를 위쪽에 띄웁니다` → `원격을 받아 받을 커밋과 올릴 커밋의 수를 <span class="b">Pull ↓n</span>·<span class="b">Push ↑n</span>처럼 두 버튼에 붙입니다`
- `<strong><span class="b">배포 스크립트</span>·<span class="b">롤백 스크립트</span>:</strong>` 다음 줄 `개발 화면에도 있지만` 앞에 `변경 목록 바로 위 줄의 <span class="b">스크립트 ▾</span> 메뉴에 있습니다.`를 더한다.
- `도구 창에 <span class="b">새 브랜치</span>·<span class="b">브랜치 전환</span> 두 버튼이` 다음 줄 `있습니다.` → `도구 창 오른쪽 위 브랜치 이름 버튼(<code>develop ▾</code>)을 누르면 <span class="b">새 브랜치</span>·<span class="b">브랜치 전환</span> 두 메뉴 항목이 나옵니다.` (두 줄을 합쳐 한 문장으로 만든다)
- `<h4>두 버튼은 개발 클론에서만 눌립니다</h4>` → `<h4>두 메뉴 항목은 개발 클론에서만 눌립니다</h4>`
- 같은 절의 `둘 다 그 클론에서는 처음부터 비활성화되어 있습니다.` → `둘 다 그 클론에서는 메뉴를 열면 잠긴 채 보입니다.`
- 2.4 절의 `<span class="b">전체 다시 추출</span>은 모든 객체를 다시 스크립팅합니다` → `<span class="b">전체 다시 추출</span>(새로고침 옆 <span class="b">▾</span> 메뉴)은 모든 객체를 다시 스크립팅합니다`

Run: `grep -n "받을 커밋 n개\|브랜치: …\|처음부터 비활성화\|DBVC 0.7.2" docs/user-guide.html`
Expected: 출력 없음.

브라우저로 `docs/user-guide.html`을 열어 그림을 본다. 버튼 글자가 사각형 밖으로 넘치거나 설명 선이 가리키는 줄과 어긋나면 해당 `x`/`width`만 고친다. 좌표를 고쳤다면 무엇을 왜 고쳤는지 커밋 메시지 본문에 적는다.

- [ ] **Step 4: 설치 체크리스트를 고친다**

`docs/setup-checklist.md`의 `### 무시와 변경 로그 보존 확인 (0.5.21)` 줄 바로 앞에 절을 더한다(스펙 4.2).

```markdown
### 도구 줄 정리 (0.9.0)

이 절은 CI가 검증하지 못하는 WPF 렌더링·메뉴를 SSMS 21에서 직접 확인한다. 개발 클론 하나와 배포 클론 하나가 필요하다.

- [ ] **밝은 테마와 어두운 테마 각각에서** 세 메뉴(**새로고침 ▾**, **스크립트 ▾**, 브랜치 이름 **▾**)를 열어 글씨가 읽히고, 항목에 마우스를 올리면 강조가 보인다
- [ ] 도구 창을 좁게 도킹하면 **Pull·Push·원격 확인** 이 셋이 함께 다음 줄로 내려간다(하나만 떨어지지 않는다). 첫 줄의 "Windows 인증" 같은 대상 표시가 버튼에 가리지 않는다
- [ ] **새로고침 ▾ → 전체 다시 추출**, **스크립트 ▾ → 배포 스크립트... / 롤백 스크립트...**, 브랜치 이름 **▾ → 브랜치 전환... / 새 브랜치...** 가 예전 버튼과 같은 결과를 낸다
- [ ] **원격 확인** 뒤 Pull·Push 버튼에 `↓n`·`↑n` 이 붙고, **Pull** 뒤와 다른 데이터베이스를 고른 뒤에는 사라진다
- [ ] 체크를 모두 풀면 **되돌리기**·**무시** 가 바로 잠기고, **스크립트 ▾** 는 열리되 두 항목이 잠겨 보인다. "체크한 항목 n개" 가 체크를 바꿀 때마다 따라온다
- [ ] 빈 커밋 메시지 칸에 회색 "커밋 메시지" 안내가 보이고, 글자를 치면 사라진다. 칸을 눌러 바로 입력할 수 있다
- [ ] **전체 다시 추출** 이 도는 동안 세 메뉴 버튼이 잠긴다
- [ ] **배포 클론:** 윗줄 브랜치 이름 **▾** 를 열면 두 항목이 잠겨 보이고, 마우스를 올리면 고정 브랜치라 잠겼다는 안내가 뜬다. 윗줄이 병합 영역 머리글(`병합 — 목적지: …`)을 가리거나 겹치지 않는다
- [ ] **배포 클론:** **병합할 브랜치 확인** 이 도는 동안 윗줄 브랜치 버튼도 잠긴다. Pull·Push·Commit 같은 개발 화면 버튼은 어디에도 보이지 않는다
- [ ] **배포 클론:** 병합 → "차이 검사 시작" 확인까지 한 바퀴가 0.8.0과 똑같이 돈다(회귀 확인 — 0.8.0 병합 절의 항목을 그대로 한 번 더 따라간다)
```

기존 항목의 버튼 이름을 새 자리로 고친다. 먼저 찾는다:

Run: `grep -n "\*\*원격 확인\*\* 의 숫자\|\*\*배포 스크립트\*\* → 저장\|\*\*롤백 스크립트\*\* 를 만들면" docs/setup-checklist.md`
Expected: 세 줄.

- `**원격 확인** 의 숫자가` → `**원격 확인** 뒤 Pull·Push 버튼에 붙는 숫자가`
- `**배포 스크립트** → 저장` → `**스크립트 ▾ → 배포 스크립트...** → 저장`
- `**롤백 스크립트** 를 만들면` → `**스크립트 ▾ → 롤백 스크립트...** 로 만들면`

병합 절(0.8.0)의 **병합할 브랜치 확인**·**병합**·**차이 검사**·**배포 스크립트 저장...** 은 이름과 자리가 그대로이므로 고치지 않는다.

- [ ] **Step 5: 전체 빌드와 테스트를 돌린다**

Run: `dotnet build DBVC.slnx`
Expected: 경고 증가 없이 성공.

Run: `dotnet test tests/DBVC.Core.Tests -f net10.0`
Run: `dotnet test tests/DBVC.Vsix.Tests -f net10.0`
Run: `dotnet test tests/DBVC.Vsix.Tests -f net48`
Expected: 셋 다 전체 PASS — 0.8.0의 `DeploymentViewModelMergeTests`, `GitManagerMergeTests`, `DeploymentPanelLayoutTests`를 포함한다. 실패가 있으면 그 출력을 그대로 보고하고 커밋하지 않는다.

Run: `git diff --stat main -- src/DBVC.Vsix/ViewModels/DeploymentViewModel.cs src/DBVC.Vsix/ViewModels/UnmergedBranchItemViewModel.cs src/DBVC.Core`
Expected: 출력 없음(스펙 2.7 — 배포 패널과 Core를 건드리지 않았다). 작업 브랜치가 아니라 main 위에서 바로 작업했다면 `main` 대신 이 계획을 시작하기 전 커밋(`git log --oneline`에서 Task 1 커밋의 부모)을 쓴다.

Run: `git diff main -- src/DBVC.Vsix/UI/ViewChangesControl.xaml | grep -n "^[-+]" | grep -in "Deployment\|병합\|Merge"`
Expected: 출력 없음(배포 패널 XAML이 바뀌지 않았다).

Run(PowerShell): `dotnet build src/DBVC.Vsix/DBVC.Vsix.csproj -c Release; dir src\DBVC.Vsix\bin\Release\net48\*.vsix`
Expected: `.vsix` 파일이 방금 시각으로 존재한다. 빌드 성공만으로는 산출물이 있다고 보지 않는다(CLAUDE.md).

- [ ] **Step 6: 커밋한다**

```bash
git add src/DBVC.Vsix/source.extension.vsixmanifest README.md docs/user-guide.html docs/setup-checklist.md
git commit -m "docs: 도구 줄 정리를 설명서·체크리스트에 싣고 0.9.0으로 올린다" -m "Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 7: 사용자에게 넘긴다**

이 계획이 건드린 것은 CI가 검증하지 않는 영역(WPF 렌더링, 메뉴, 테마)이다. `.vsix` 경로와 함께 Step 4에 더한 체크리스트 절을 사용자에게 알리고, **SSMS 21에서 개발 클론과 배포 클론 양쪽으로 그 절을 눌러 보기 전에는 "동작한다"고 보고하지 않는다.**
