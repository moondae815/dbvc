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
