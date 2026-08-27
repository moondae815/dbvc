using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// DBA가 운영 DB에 붙었다가 초기화 버튼을 한 번 누르면 금지된 DDL 트리거가 설치된다.
    /// 그 버튼이 있는 화면이 뜨지 않게 하는 것이 이 판정의 존재 이유다.
    /// </summary>
    [TestFixture]
    public class PanelSelectorTests
    {
        [Test]
        public void Select_ShowsChangeList_WhenWriteAndInitialized()
        {
            Assert.That(PanelSelector.Select(MappingMode.Write, isInitialized: true),
                Is.EqualTo(DbvcPanelKind.ChangeList));
        }

        [Test]
        public void Select_ShowsSetupOverlay_WhenWriteAndNotInitialized()
        {
            Assert.That(PanelSelector.Select(MappingMode.Write, isInitialized: false),
                Is.EqualTo(DbvcPanelKind.SetupOverlay));
        }

        [TestCase(MappingMode.Deploy, true)]
        [TestCase(MappingMode.Deploy, false)]
        [TestCase(MappingMode.Audit, true)]
        [TestCase(MappingMode.Audit, false)]
        public void Select_ShowsDeploymentPanel_RegardlessOfInitialization_WhenModeIsNotWrite(MappingMode mode, bool isInitialized)
        {
            // 초기화 여부를 보면 안 된다. 운영 DB는 미초기화 상태가 정상이고,
            // 그때 오버레이가 뜨면 눌리는 버튼이 바로 금지된 트리거 설치다.
            Assert.That(PanelSelector.Select(mode, isInitialized), Is.EqualTo(DbvcPanelKind.DeploymentPanel));
        }
    }
}
