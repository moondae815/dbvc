#if NETFRAMEWORK
using System.Windows;
using System.Windows.Controls;
using Moq;
using NUnit.Framework;
using DBVC.Core;
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
    }
}
#endif
