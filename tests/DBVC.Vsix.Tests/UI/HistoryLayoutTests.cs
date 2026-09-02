#if NETFRAMEWORK
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.UI;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 전체 이력 보기 모드에서 3-pane 레이아웃(이력 목록 -> 변경된 파일 목록 -> diff) 구성 및
    /// ChangedFilesListView의 배치/컬럼 정의를 검증한다.
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
    }
}
#endif
