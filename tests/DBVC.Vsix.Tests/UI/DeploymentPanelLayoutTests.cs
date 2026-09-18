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

        /// <summary>
        /// 병합 영역 머리글은 Expander의 머리 단추 안에 있다. 그 템플릿이 자기 글자색을 박아 두어
        /// 어두운 테마에서 "병합 — 목적지: ..."가 배경에 묻혔다(2026-09-18 SSMS 21 실기).
        /// 색을 요소에 직접 줘야 한다.
        /// </summary>
        [Test]
        public void MergeHeader_TakesItsForegroundFromTheShellTheme()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy);
            control.Resources[Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey] =
                System.Windows.Media.Brushes.Magenta;

            LayoutAt(control, 700);

            var header = (System.Windows.Controls.TextBlock)control.FindName("MergeHeaderText");
            Assert.That(header, Is.Not.Null, "XAML에 MergeHeaderText가 있어야 한다");
            Assert.That(header.Foreground, Is.SameAs(System.Windows.Media.Brushes.Magenta));
        }

        /// <summary>
        /// 작업 중에는 목록을 잠그는 대신 클릭과 포커스만 막는다. IsEnabled를 끄면 WPF 기본
        /// 템플릿이 배경 속성을 무시하고 #F4F4F4로 칠해, 어두운 테마에서 작업이 도는 동안만
        /// 목록이 흰 바탕으로 바뀌었다(2026-09-18 SSMS 21 실기 + 픽셀 측정으로 확인).
        /// </summary>
        [Test]
        public void ListsBlockInputWithoutDisabling_WhileWorkIsRunning()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy);
            var vm = (DBVC.Vsix.ViewModels.ViewChangesViewModel)control.DataContext;
            vm.Deployment.Busy.IsBusy = true;

            LayoutAt(control, 700);

            foreach (var name in new[] { "MergeBranchList", "DeploymentDifferenceList" })
            {
                var list = (System.Windows.Controls.ListView)control.FindName(name);
                Assert.That(list.IsEnabled, Is.True, $"{name}을 잠그면 기본 템플릿이 흰 바탕으로 칠한다");
                Assert.That(list.IsHitTestVisible, Is.False, $"{name}은 작업 중 클릭이 막혀야 한다");
                Assert.That(list.Focusable, Is.False, $"{name}은 작업 중 키보드 선택도 막혀야 한다");
            }
        }

        /// <summary>
        /// 병합 브랜치 목록은 다섯 열(브랜치·작성자·마지막 커밋·커밋 수·테스트 반영)로 결정을
        /// 돕는데, 미리보기를 옆에 두었더니 도킹한 창에서 작성자 뒤가 잘렸다(0.9.1 실기 확인).
        /// 미리보기를 아래로 내려 목록이 패널 폭을 다 쓰게 한다.
        /// </summary>
        [Test]
        public void MergePreview_SitsBelowTheBranchList_NotBesideIt()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "develop", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy);

            LayoutAt(control, 700);

            var list = (FrameworkElement)control.FindName("MergeBranchList");
            var preview = (FrameworkElement)control.FindName("MergePreview");
            Assert.That(list, Is.Not.Null, "XAML에 MergeBranchList가 있어야 한다");
            Assert.That(preview, Is.Not.Null, "XAML에 MergePreview가 있어야 한다");

            var listTop = list.TranslatePoint(new Point(0, 0), control).Y;
            var previewTop = preview.TranslatePoint(new Point(0, 0), control).Y;

            Assert.That(previewTop, Is.GreaterThan(listTop + 20), "미리보기는 목록 아래에 와야 한다");
            Assert.That(list.ActualWidth, Is.GreaterThan(560),
                "목록이 패널 폭을 다 써야 다섯 열이 잘리지 않는다");
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

        /// <summary>
        /// 작업 중에는 차이 목록을 고를 수 없어야 한다.
        ///
        /// 항목을 고르면 원문 읽기가 같은 BusyState를 쓰는데, 그것은 차이 검사보다 빨리 끝나
        /// 먼저 IsBusy를 내려놓는다 - 그러면 검사가 아직 도는 중에 두 버튼이 되살아나고,
        /// 다시 누른 검사가 같은 대상을 상대로 겹쳐 돈다. BusyState를 하나로 뽑은 이유가
        /// 바로 그 겹침을 막는 것이므로, 여기가 깨지면 그 근거가 사라진다.
        ///
        /// 마크업이 아니라 렌더링된 값을 본다 - 이 패널의 안쪽 Grid는 DataContext가
        /// Deployment라, 경로를 잘못 쓰면 바인딩이 조용히 실패하고 기본값(사용 가능)이 남는다.
        /// </summary>
        [Test]
        public void DifferenceList_IsNotInteractive_WhileBackgroundWorkRuns()
        {
            var control = ViewChangesControlFixtures.NewConnectedControl(
                new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None },
                mode: MappingMode.Deploy,
                installedVersion: 0);

            var list = (ListView)control.FindName("DeploymentDifferenceList");

            LayoutAt(control, 600);
            Assert.That(list.IsHitTestVisible, Is.True,
                "전제가 깨졌다 - 평소에도 막혀 있으면 막는 것을 확인할 수 없다");

            var viewModel = (DBVC.Vsix.ViewModels.ViewChangesViewModel)control.DataContext;
            viewModel.Deployment.Busy.IsBusy = true;
            LayoutAt(control, 600);

            // 막는 방법이 IsEnabled에서 클릭·포커스 차단으로 바뀌었다(0.9.4) - 잠그면 WPF 기본
            // 템플릿이 배경을 무시하고 흰 바탕으로 칠한다. 막는다는 요구는 그대로다.
            Assert.That(list.IsHitTestVisible, Is.False,
                "차이 검사가 도는 중에 목록을 고르면 원문 읽기가 먼저 IsBusy를 내려놓아 검사가 겹쳐 돈다");
            Assert.That(list.Focusable, Is.False,
                "키보드로 고르는 길도 함께 막아야 한다");
        }
    }
}
#endif
