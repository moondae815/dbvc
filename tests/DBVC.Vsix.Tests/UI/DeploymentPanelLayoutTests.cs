#if NETFRAMEWORK
using System.Windows;
using System.Windows.Controls;
using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.UI;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 본문 자리에 뜨는 세 화면(변경 목록·초기화 오버레이·배포 패널) 중 무엇이 실제로
    /// 보이는지. TopRowLayoutTests와 같은 방식으로 실제 컨트롤을 STA로 배치해 확인한다 -
    /// 렌더링은 CI가 못 보지만 어느 Grid가 Visible인지는 볼 수 있다.
    ///
    /// 가장 중요한 것은 운영·테스트 대상(Audit·Deploy)이 미초기화 상태에서 초기화
    /// 오버레이를 보지 않는다는 사실이다 - 그 오버레이의 버튼은 금지된 DDL 트리거 설치라서,
    /// 여기서 잘못 뜨면 사용자가 누르는 순간 조직 규정을 어긴다.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class DeploymentPanelLayoutTests
    {
        private static void LayoutAt(ViewChangesControl control, double width)
        {
            control.Measure(new Size(width, double.PositiveInfinity));
            control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
            control.UpdateLayout();
        }

        private static bool IsVisible(ViewChangesControl control, string name)
        {
            var element = (UIElement)control.FindName(name);
            return element.Visibility == Visibility.Visible;
        }

        /// <summary>
        /// Audit 대상은 미초기화가 정상 상태다(운영 DB에는 트리거를 설치하지 않는다).
        /// 그 상태에서도 배포 패널이 뜨고, 초기화 오버레이는 뜨지 않아야 한다 - 이 테스트가
        /// 깨지면 곧 사용자가 운영 DB에 DDL 트리거를 설치하는 버튼을 보게 된다는 뜻이다.
        /// </summary>
        [Test]
        public void AuditTarget_ShowsDeploymentPanel_NotSetupOverlay_WhenUninitialized()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Audit,
                installedVersion: 0);

            LayoutAt(control, 600);

            Assert.That(IsVisible(control, "DeploymentPanelGrid"), Is.True,
                "Audit 대상은 미초기화 여부와 무관하게 배포 패널을 보여야 한다");
            Assert.That(IsVisible(control, "SetupOverlayGrid"), Is.False,
                "Audit 대상에서 초기화 오버레이가 뜨면 그 버튼이 곧 금지된 DDL 트리거 설치다");
            Assert.That(IsVisible(control, "ChangeListGrid"), Is.False);
        }

        /// <summary>Deploy(테스트) 대상도 Audit과 같은 이유로 같은 화면을 봐야 한다.</summary>
        [Test]
        public void DeployTarget_ShowsDeploymentPanel_NotSetupOverlay_WhenUninitialized()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy,
                installedVersion: 0);

            LayoutAt(control, 600);

            Assert.That(IsVisible(control, "DeploymentPanelGrid"), Is.True);
            Assert.That(IsVisible(control, "SetupOverlayGrid"), Is.False);
        }

        /// <summary>Write(개발) 대상이 초기화되어 있으면 지금까지처럼 변경 목록을 본다.</summary>
        [Test]
        public void WriteTarget_ShowsChangeList_WhenInitialized()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Write,
                installedVersion: DBVC.Core.StateTracker.RequiredSchemaVersion);

            LayoutAt(control, 600);

            Assert.That(IsVisible(control, "ChangeListGrid"), Is.True);
            Assert.That(IsVisible(control, "SetupOverlayGrid"), Is.False);
            Assert.That(IsVisible(control, "DeploymentPanelGrid"), Is.False);
        }

        /// <summary>Write(개발) 대상이 미초기화면 지금까지처럼 초기화 오버레이를 본다.</summary>
        [Test]
        public void WriteTarget_ShowsSetupOverlay_WhenUninitialized()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Write,
                installedVersion: 0);

            LayoutAt(control, 600);

            Assert.That(IsVisible(control, "SetupOverlayGrid"), Is.True);
            Assert.That(IsVisible(control, "ChangeListGrid"), Is.False);
            Assert.That(IsVisible(control, "DeploymentPanelGrid"), Is.False);
        }

        /// <summary>
        /// 차단 오버레이는 초기화 오버레이뿐 아니라 배포 패널도 덮어야 한다. 배포·감사 대상도
        /// 브랜치 고정 위반이면 잘못된 기준으로 비교한 결과를 사용자에게 보여줄 수 없다.
        /// </summary>
        [Test]
        public void BlockOverlay_CoversDeploymentPanel_WhenBlocked()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(new RepositoryState
            {
                CurrentBranch = "develop",
                BlockReason = RepositoryBlockReason.BranchMismatch,
                BlockMessage = "이 대상은 'master' 브랜치에 고정되어 있는데 저장소는 'develop'에 있습니다."
            }, mode: MappingMode.Audit, installedVersion: 0);

            LayoutAt(control, 600);

            Assert.That(IsVisible(control, "DeploymentPanelGrid"), Is.True,
                "배포 패널 자체는 여전히 트리에 있어야 한다 - 오버레이가 그 위를 덮는 것이다");

            var overlay = (Border)control.FindName("BlockOverlay");
            Assert.That(overlay.Visibility, Is.EqualTo(Visibility.Visible),
                "배포·감사 대상도 차단되면 오버레이가 보여야 한다");
        }
    }
}
#endif
