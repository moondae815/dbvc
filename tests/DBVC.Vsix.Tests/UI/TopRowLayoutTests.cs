#if NETFRAMEWORK
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;
using DBVC.Vsix.UI;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 최상단 행의 배치. WPF 레이아웃은 CI가 검증하지 않는 영역이라 SSMS에서 눌러 보기 전에는
    /// 결함이 드러나지 않았다 — 실제로 버전 표시가 두 줄 사이에 떠 있는 것을 그렇게 발견했다.
    /// 여기서는 실제 컨트롤을 STA로 배치해 좌표를 직접 본다.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class TopRowLayoutTests
    {
        private static ViewChangesControl NewControl()
        {
            var vm = new ViewChangesViewModel(
                Mock.Of<IConfigManager>(), Mock.Of<IStateTracker>(), Mock.Of<IGitManager>(),
                Mock.Of<ISmoManager>(), Mock.Of<IUserNotifier>(), Mock.Of<IFileSaveDialog>(),
                Mock.Of<IWorkingTreeCleaner>(), Mock.Of<IFolderBrowseDialog>(),
                Mock.Of<ISqlCredentialStore>(), Mock.Of<ISsmsConnectionSource>());
            return new ViewChangesControl(vm, null);
        }

        /// <summary>
        /// 개체 탐색기가 대상을 내주는 상태로 만들고 Connect까지 누른 컨트롤. 기본 스케줄러가
        /// 인라인이라 이 호출이 끝나면 저장소 상태 판정도 끝나 있다.
        /// </summary>
        private static ViewChangesControl NewConnectedControl(RepositoryState repositoryState)
        {
            var config = new Mock<IConfigManager>();
            config.Setup(c => c.TryGetMapping(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new MappingConfig { ServerName = "S", DatabaseName = "D", GitPath = @"C:epo" });

            var tracker = new Mock<IStateTracker>();
            tracker.Setup(t => t.TestConnection(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
            tracker.Setup(t => t.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(StateTracker.RequiredSchemaVersion);
            tracker.Setup(t => t.RefreshState(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Returns(true);
            tracker.Setup(t => t.GetPendingChanges(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new System.Collections.Generic.List<ChangeRecord>());

            var git = new Mock<IGitManager>();
            git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>())).Returns(repositoryState);

            var ssms = new Mock<ISsmsConnectionSource>();
            ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S", "D", SqlAuthMode.Windows, null, null, null));

            var vm = new ViewChangesViewModel(
                config.Object, tracker.Object, git.Object, Mock.Of<ISmoManager>(), Mock.Of<IUserNotifier>(),
                Mock.Of<IFileSaveDialog>(), Mock.Of<IWorkingTreeCleaner>(), Mock.Of<IFolderBrowseDialog>(),
                Mock.Of<ISqlCredentialStore>(), ssms.Object);
            vm.ConnectCommand.Execute(null);
            return new ViewChangesControl(vm, null);
        }

        private static Point TopLeftOf(ViewChangesControl control, string name)
        {
            var element = (FrameworkElement)control.FindName(name);
            return element.TranslatePoint(new Point(0, 0), control);
        }

        private static void LayoutAt(ViewChangesControl control, double width)
        {
            control.Measure(new Size(width, double.PositiveInfinity));
            control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
            control.UpdateLayout();
        }

        /// <summary>
        /// 버전은 오른쪽 "위"에 있어야 한다. 바깥 DockPanel에 붙어 있으므로, 안쪽 WrapPanel이
        /// 두 줄로 늘어나면 세로 정렬 기준이 "두 줄을 합친 높이"가 되어 줄 사이에 뜬다.
        /// 줄바꿈 전후로 세로 위치가 그대로인지가 그 결함을 가르는 지점이다.
        /// </summary>
        [Test]
        public void VersionLabel_StaysOnTheFirstLine_WhenTheTopRowWraps()
        {
            var control = NewControl();

            LayoutAt(control, 600);
            var versionWhenWide = TopLeftOf(control, "VersionLabel").Y;
            Assert.That(TopLeftOf(control, "TargetLabel").Y,
                Is.EqualTo(TopLeftOf(control, "ConnectButton").Y).Within(6),
                "600px에서는 한 줄이어야 한다. 아니면 이 테스트의 전제가 틀렸다.");

            LayoutAt(control, 150);
            var connectTop = TopLeftOf(control, "ConnectButton").Y;
            Assert.That(TopLeftOf(control, "TargetLabel").Y, Is.GreaterThan(connectTop + 6),
                "150px에서는 대상 표시가 다음 줄로 내려가야 한다. 아니면 이 테스트의 전제가 틀렸다.");

            Assert.That(TopLeftOf(control, "VersionLabel").Y, Is.EqualTo(versionWhenWide).Within(1),
                "줄바꿈이 일어나도 버전은 첫째 줄에 그대로 있어야 한다.");
        }

        /// <summary>
        /// 브랜치는 버전 왼쪽, 같은 첫째 줄에 있어야 한다. DockPanel은 먼저 Dock된 것이 더
        /// 바깥이라 XAML에서 두 줄의 순서를 뒤집으면 브랜치가 버전 오른쪽으로 밀린다 -
        /// 눈으로만 보면 놓치는 종류의 실수라 좌표로 못박는다.
        /// </summary>
        [Test]
        public void BranchLabel_SitsLeftOfTheVersion_OnTheFirstLine()
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = "feature/x", BlockReason = RepositoryBlockReason.None });

            LayoutAt(control, 600);

            var branch = TopLeftOf(control, "BranchLabel");
            var version = TopLeftOf(control, "VersionLabel");

            Assert.That(branch.X, Is.LessThan(version.X), "브랜치가 버전 왼쪽에 와야 한다");
            Assert.That(branch.Y, Is.EqualTo(version.Y).Within(1), "둘은 같은 줄에 있어야 한다");
        }

        /// <summary>
        /// 브랜치를 알 수 없으면 표시가 아예 없어야 한다. "브랜치: " 만 남으면 오해를 준다.
        /// </summary>
        [Test]
        public void BranchLabel_IsHidden_WhenThereIsNoBranch()
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = null, BlockReason = RepositoryBlockReason.None });

            LayoutAt(control, 600);

            var branch = (FrameworkElement)control.FindName("BranchLabel");
            Assert.That(branch.Visibility, Is.Not.EqualTo(Visibility.Visible));
        }

        /// <summary>
        /// 차단 오버레이는 도구 줄까지 덮어야 한다. 초기화 오버레이처럼 내용 행만 덮으면
        /// Pull·배포 스크립트 같은 버튼이 어긋난 저장소를 상대로 그대로 눌린다.
        /// </summary>
        [Test]
        public void BlockOverlay_CoversTheToolbarToo_WhenBlocked()
        {
            var control = NewConnectedControl(new RepositoryState
            {
                CurrentBranch = "develop",
                BlockReason = RepositoryBlockReason.BranchMismatch,
                BlockMessage = "이 대상은 'master' 브랜치에 고정되어 있는데 저장소는 'develop'에 있습니다."
            });

            LayoutAt(control, 600);

            var overlay = FindOverlay(control);
            Assert.That(overlay, Is.Not.Null, "차단 상태에서는 오버레이가 보여야 한다");

            var top = overlay!.TranslatePoint(new Point(0, 0), control);
            Assert.That(top.Y, Is.EqualTo(0).Within(1), "오버레이가 첫 행부터 덮어야 한다");
            Assert.That(overlay.ActualHeight, Is.EqualTo(control.ActualHeight).Within(1),
                "오버레이가 컨트롤 전체 높이를 덮어야 한다");
        }

        /// <summary>차단이 아니면 오버레이는 보이지 않아야 한다.</summary>
        [Test]
        public void BlockOverlay_IsHidden_WhenNotBlocked()
        {
            var control = NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None });

            LayoutAt(control, 600);

            Assert.That(FindOverlay(control), Is.Null);
        }

        /// <summary>
        /// 차단 오버레이는 색을 셸 테마에서 받는데, 셸 없이 도는 이 테스트에서는 그 키가 풀리지
        /// 않아 Background가 null이 된다. 배경이 null인 WPF 요소는 마우스 이벤트를 그대로
        /// 통과시키므로, 그 상태로 히트 테스트를 하면 SSMS에서와 다른 결과가 나온다.
        /// 셸이 주는 브러시를 흉내내 키를 채워 넣고 판단한다.
        /// </summary>
        private static void SupplyShellBrushes(ViewChangesControl control)
        {
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBackgroundKey] = Brushes.White;
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey] = Brushes.Black;
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.GrayTextKey] = Brushes.Gray;
        }

        /// <summary>
        /// 차단은 "덮여 보인다"가 아니라 "눌리지 않는다"여야 한다. Commit은 CanCommit이 막지만
        /// Pull·Push·배포 스크립트에는 그런 판정이 없어서, 그 버튼들을 막는 것은 오버레이가
        /// 마우스 이벤트를 흡수하는 것뿐이다.
        /// </summary>
        [Test]
        public void BlockOverlay_SwallowsClicksOnTheToolbar_WhenBlocked()
        {
            var control = NewConnectedControl(new RepositoryState
            {
                CurrentBranch = "develop",
                BlockReason = RepositoryBlockReason.BranchMismatch,
                BlockMessage = "이 대상은 'master' 브랜치에 고정되어 있는데 저장소는 'develop'에 있습니다."
            });
            SupplyShellBrushes(control);

            LayoutAt(control, 600);

            var overlay = FindOverlay(control);
            Assert.That(overlay, Is.Not.Null, "차단 상태에서는 오버레이가 보여야 한다");

            // 도구 줄 한복판. 차단이 아니면 여기에 버튼이 있다.
            var point = new Point(40, overlay!.TranslatePoint(new Point(0, 0), control).Y + 10);
            var hit = VisualTreeHelper.HitTest(control, point);

            Assert.That(hit, Is.Not.Null, "히트 테스트가 아무것도 잡지 못했다면 판정이 성립하지 않는다");
            Assert.That(IsInside(hit!.VisualHit, overlay), Is.True,
                "오버레이가 아니라 그 아래 요소가 잡혔다 - 덮여 보이기만 하고 클릭은 통과한다");
        }

        private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = VisualTreeHelper.GetParent(node);
            }

            return false;
        }

        /// <summary>보이는 상태일 때만 돌려준다 - 숨은 요소는 좌표를 물어도 의미가 없다.</summary>
        private static Border? FindOverlay(ViewChangesControl control)
        {
            var overlay = (Border)control.FindName("BlockOverlay");
            return overlay.Visibility == Visibility.Visible ? overlay : null;
        }
    }
}
#endif
