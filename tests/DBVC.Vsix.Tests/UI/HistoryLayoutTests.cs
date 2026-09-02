#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.UI;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 전체 이력 보기 모드에서 3-pane 레이아웃(이력 목록 -> 변경된 파일 목록 -> diff) 구성,
    /// ChangedFilesListView의 배치/컬럼 정의, 그리고 단일 객체 필터·Diff 유무에 따른
    /// 행 접힘/펼침(UpdateHistoryRowHeights)을 검증한다.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class HistoryLayoutTests
    {
        private static void LayoutAt(ViewChangesControl control, double width)
        {
            control.Measure(new Size(width, double.PositiveInfinity));
            control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
            control.UpdateLayout();
        }

        /// <summary>
        /// Measure/Arrange만으로는 Loaded가 뜨지 않는다 - 이 컨트롤은 어느 PresentationSource에도
        /// 붙지 않은 고아 트리라서다. UpdateHistoryRowHeights는 OnLoaded 경로로만 처음 걸리므로
        /// 행 접힘 동작을 테스트하려면 여기서 직접 이벤트를 올려야 한다.
        /// </summary>
        private static void RaiseLoaded(ViewChangesControl control)
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        }

        private static ViewChangesViewModel ViewModelOf(ViewChangesControl control)
            => (ViewChangesViewModel)control.DataContext;

        [Test]
        public void ChangedFilesListView_Exists_WithCorrectGridRowAndBindings()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Write,
                installedVersion: DBVC.Core.StateTracker.RequiredSchemaVersion);

            LayoutAt(control, 800);

            // ChangedFilesListView는 필터 모드에서 행째 접히는 DockPanel(ChangedFilesPanel) 안에
            // 있다 - 그 DockPanel이 Grid.Row 2를 지니고, 안쪽 ListView 자체는 Grid.Row를 두지 않는다.
            var changedFilesPanel = (DockPanel)control.FindName("ChangedFilesPanel");
            Assert.That(changedFilesPanel, Is.Not.Null, "ChangedFilesPanel이 XAML에 존재해야 한다.");
            Assert.That(Grid.GetRow(changedFilesPanel), Is.EqualTo(2), "ChangedFilesPanel은 Grid.Row 2에 위치해야 한다.");

            var changedFilesList = (ListView)control.FindName("ChangedFilesListView");
            Assert.That(changedFilesList, Is.Not.Null, "ChangedFilesListView가 XAML에 존재해야 한다.");

            var historyList = (ListView)control.FindName("HistoryListView");
            Assert.That(historyList, Is.Not.Null, "HistoryListView가 XAML에 존재해야 한다.");
            Assert.That(Grid.GetRow(historyList), Is.EqualTo(0), "HistoryListView는 Grid.Row 0에 위치해야 한다.");

            // 컬럼 검증
            var gridView = changedFilesList.View as GridView;
            Assert.That(gridView, Is.Not.Null, "ChangedFilesListView는 GridView 형태여야 한다.");
            Assert.That(gridView!.Columns.Count, Is.EqualTo(3), "상태, 객체 유형, 객체명 3개의 컬럼이 있어야 한다.");

            Assert.That(gridView.Columns[0].Header, Is.EqualTo("상태"));
            Assert.That(gridView.Columns[1].Header, Is.EqualTo("객체 유형"));
            Assert.That(gridView.Columns[2].Header, Is.EqualTo("객체명"));

            var stateBinding = gridView.Columns[0].DisplayMemberBinding as Binding;
            Assert.That(stateBinding?.Path.Path, Is.EqualTo("StateText"));

            // 위 변경 목록(Changes)도 ObjectTypeText를 쓴다 - 같은 헤더 아래 두 목록이 다른 값을
            // 보이는 것이 이 결함의 원인이었으므로 원본 ObjectType이 아니라 이쪽을 바인딩한다.
            var typeBinding = gridView.Columns[1].DisplayMemberBinding as Binding;
            Assert.That(typeBinding?.Path.Path, Is.EqualTo("ObjectTypeText"));

            var nameBinding = gridView.Columns[2].DisplayMemberBinding as Binding;
            Assert.That(nameBinding?.Path.Path, Is.EqualTo("ObjectName"));
        }

        [Test]
        public void ChangedFilesSplitterAndHistoryDiffPanel_AreAtExpectedGridRows()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Write,
                installedVersion: DBVC.Core.StateTracker.RequiredSchemaVersion);

            LayoutAt(control, 800);

            Assert.That(Grid.GetRow(control.ChangedFilesSplitter), Is.EqualTo(3),
                "ChangedFilesSplitter는 변경 파일 목록과 Diff 사이인 Grid.Row 3에 있어야 한다.");
            Assert.That(Grid.GetRow(control.HistoryDiffPanel), Is.EqualTo(4),
                "HistoryDiffPanel은 맨 아래 Grid.Row 4에 있어야 한다.");
        }

        [Test]
        public void UpdateHistoryRowHeights_CollapsesChangedFilesRow_WhenSingleObjectMode()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Write,
                installedVersion: DBVC.Core.StateTracker.RequiredSchemaVersion);

            LayoutAt(control, 800);
            RaiseLoaded(control);

            var vm = ViewModelOf(control);

            // 특정 객체로 좁히면(경로를 준 Load) IsSingleObjectMode가 켜진다 - 그 목록은
            // 이미 하나로 정해져 있으므로 변경 파일 목록 행이 접혀야 한다.
            vm.History.Load(vm.ServerName, vm.DatabaseName, "dbo/Table/Foo.sql");

            Assert.That(control.ChangedFilesRow.Height.Value, Is.EqualTo(0),
                "단일 객체 모드에서는 변경 파일 목록 행이 접혀야 한다.");
            Assert.That(control.ChangedFilesSplitterRow.Height.Value, Is.EqualTo(0),
                "단일 객체 모드에서는 그 분할선 행도 접혀야 한다.");
            Assert.That(control.ChangedFilesPanel.Visibility, Is.EqualTo(Visibility.Collapsed),
                "ChangedFilesPanel도 접혀서 숨어야 한다.");
            Assert.That(control.HistoryListSplitter.Visibility, Is.EqualTo(Visibility.Collapsed),
                "이력 목록⇄변경 파일 목록 분할선은 접힌 행 쪽으로 끌리면 안 되므로 함께 숨어야 한다.");
        }

        [Test]
        public void UpdateHistoryRowHeights_RestoresChangedFilesRow_WhenReturningToWholeRepository()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Write,
                installedVersion: DBVC.Core.StateTracker.RequiredSchemaVersion);

            LayoutAt(control, 800);
            RaiseLoaded(control);

            var vm = ViewModelOf(control);

            vm.History.Load(vm.ServerName, vm.DatabaseName, "dbo/Table/Foo.sql");
            Assert.That(control.ChangedFilesRow.Height.Value, Is.EqualTo(0), "전제 조건: 먼저 접혀 있어야 한다.");

            // "전체 이력으로"가 하는 것과 같다 - 경로 없이 다시 읽으면 저장소 전체 모드로 돌아온다.
            vm.History.Load(vm.ServerName, vm.DatabaseName, null);

            Assert.That(control.ChangedFilesRow.Height.IsStar, Is.True,
                "저장소 전체 모드로 돌아오면 변경 파일 목록 행이 다시 펼쳐져야 한다.");
            Assert.That(control.ChangedFilesSplitterRow.Height.Value, Is.EqualTo(5),
                "그 분할선 행도 원래 두께로 돌아와야 한다.");
            Assert.That(control.ChangedFilesPanel.Visibility, Is.EqualTo(Visibility.Visible),
                "ChangedFilesPanel도 다시 보여야 한다.");
            Assert.That(control.HistoryListSplitter.Visibility, Is.EqualTo(Visibility.Visible),
                "이력 목록⇄변경 파일 목록 분할선도 다시 보여야 한다.");
        }

        /// <summary>
        /// 회귀 방지: UpdateHistoryRowHeights는 GridSplitter와 같은 RowDefinition.Height를 쓴다.
        /// 접힘↔펼침 상태가 그대로인데 커밋을 바꿔 골라도(= SelectedDiffModel만 바뀌어도) 사용자가
        /// 끌어 둔 분할선 위치가 1*로 되돌아오면 그 분할선은 사실상 못 쓰는 것과 같다.
        /// </summary>
        [Test]
        public void UpdateHistoryRowHeights_PreservesUserResizedHeight_WhenCollapseStateUnchanged()
        {
            var firstDetail = new CommitDetail
            {
                OldText = "old-1",
                NewText = "new-1",
                ChangedFiles = new List<HistoryChangedFile>
                {
                    new HistoryChangedFile { State = HistoryChangedFileState.Modified, RelativePath = "dbo/Table/Foo.sql" }
                }
            };
            var secondDetail = new CommitDetail
            {
                OldText = "old-2",
                NewText = "new-2",
                ChangedFiles = new List<HistoryChangedFile>
                {
                    new HistoryChangedFile { State = HistoryChangedFileState.Modified, RelativePath = "dbo/Table/Foo.sql" }
                }
            };

            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Write,
                installedVersion: DBVC.Core.StateTracker.RequiredSchemaVersion,
                configureGitManager: git =>
                {
                    git.Setup(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                        .Returns(new List<CommitInfo>
                        {
                            new CommitInfo { Sha = "sha-one", ParentCount = 1, Message = "first", Author = "a", Date = DateTimeOffset.Now },
                            new CommitInfo { Sha = "sha-two", ParentCount = 1, Message = "second", Author = "a", Date = DateTimeOffset.Now }
                        });
                    git.Setup(g => g.GetCommitDetail(It.IsAny<string>(), It.IsAny<string>(), "sha-one", It.IsAny<string>()))
                        .Returns(firstDetail);
                    git.Setup(g => g.GetCommitDetail(It.IsAny<string>(), It.IsAny<string>(), "sha-two", It.IsAny<string>()))
                        .Returns(secondDetail);
                });

            LayoutAt(control, 800);
            RaiseLoaded(control);

            var vm = ViewModelOf(control);
            vm.History.Load(vm.ServerName, vm.DatabaseName, null);

            // 전체 이력 모드에서 Diff는 커밋뿐 아니라 그 안의 변경 파일까지 골라야 뜬다
            // (ObjectHistoryViewModel.UpdateDiffModel - IsSingleObjectMode가 아니면 대상 경로를
            // SelectedChangedFile에서 가져온다).
            vm.History.SelectedEntry = vm.History.Entries[0];
            vm.History.SelectedChangedFile = vm.History.ChangedFiles[0];
            Assert.That(control.HistoryDiffRow.Height.IsStar, Is.True, "전제 조건: 첫 선택으로 Diff 행이 펼쳐져야 한다.");

            // GridSplitter가 드래그로 쓰는 것과 같은 대입이다 - 사용자가 끌어 둔 비율을 흉내 낸다.
            var draggedHeight = new GridLength(2, GridUnitType.Star);
            control.ChangedFilesRow.Height = draggedHeight;

            // 접힘↔펼침 상태는 바뀌지 않는(둘 다 Diff가 있는) 다른 커밋을 고른다. 커밋을 바꾸면
            // SelectedChangedFile은 일단 비었다가 다시 골라야 하므로(위 주석과 같은 이유),
            // 그 과정에서 ChangedFilesRow 값이 스쳐 지나가는 hasDiff 변화에 휩쓸리지 않는지도 같이 본다.
            vm.History.SelectedEntry = vm.History.Entries[1];
            vm.History.SelectedChangedFile = vm.History.ChangedFiles[0];

            Assert.That(control.ChangedFilesRow.Height, Is.EqualTo(draggedHeight),
                "접힘 상태가 그대로면 커밋을 바꿔도 사용자가 끌어 둔 높이가 유지되어야 한다.");
        }
    }
}
#endif
