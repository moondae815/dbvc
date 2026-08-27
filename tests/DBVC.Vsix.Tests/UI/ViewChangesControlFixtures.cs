#if NETFRAMEWORK
using System.Collections.Generic;
using Moq;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;
using DBVC.Vsix.UI;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.UI
{
    /// <summary>
    /// 레이아웃 픽스처들이 공유하는 컨트롤 조립 헬퍼. 대상 판정(Connect가 IsInitialized·Mode를
    /// 확정하는 절차)을 픽스처마다 따로 흉내 내면 한쪽만 최신 상태로 남고 갈라진다 -
    /// TopRowLayoutTests에 있던 것을 DeploymentPanelLayoutTests와 함께 쓰려고 여기로 뽑았다.
    /// </summary>
    internal static class ViewChangesControlFixtures
    {
        public static ViewChangesControl NewControl()
        {
            var vm = new ViewChangesViewModel(
                Mock.Of<IConfigManager>(), Mock.Of<IStateTracker>(), Mock.Of<IGitManager>(),
                Mock.Of<ISmoManager>(), Mock.Of<IUserNotifier>(), Mock.Of<IFileSaveDialog>(),
                Mock.Of<IWorkingTreeCleaner>(), Mock.Of<IRepositoryConnectDialog>(),
                Mock.Of<ISqlCredentialStore>(), Mock.Of<ISsmsConnectionSource>());
            return new ViewChangesControl(vm, null);
        }

        /// <summary>
        /// 개체 탐색기가 대상을 내주는 상태로 만들고 Connect까지 누른 컨트롤. 기본 스케줄러가
        /// 인라인이라 이 호출이 끝나면 저장소 상태 판정도 끝나 있다.
        /// </summary>
        /// <param name="mode">대상의 매핑 용도. 기본값은 Write(개발) - 배포·감사 화면을 보려면
        /// 명시적으로 Deploy·Audit을 넘긴다.</param>
        /// <param name="installedVersion">DDL 트리거 설치 버전. 0이면 미초기화다 -
        /// 운영·테스트 대상은 이것이 정상 상태이므로 기본값을 0으로 둔다.</param>
        public static ViewChangesControl NewConnectedControl(
            RepositoryState repositoryState,
            RemoteStatus? remoteStatus = null,
            MappingMode mode = MappingMode.Write,
            int installedVersion = 0)
        {
            var config = new Mock<IConfigManager>();
            config.Setup(c => c.TryGetMapping(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new MappingConfig { ServerName = "S", DatabaseName = "D", GitPath = @"C:epo", Mode = mode });

            var tracker = new Mock<IStateTracker>();
            tracker.Setup(t => t.TestConnection(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
            tracker.Setup(t => t.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(installedVersion);
            tracker.Setup(t => t.RefreshState(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Returns(true);
            tracker.Setup(t => t.GetPendingChanges(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new List<ChangeRecord>());

            var git = new Mock<IGitManager>();
            git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>())).Returns(repositoryState);
            if (remoteStatus != null)
            {
                git.Setup(g => g.FetchRemoteStatus(It.IsAny<string>(), It.IsAny<string>())).Returns(remoteStatus);
            }

            var ssms = new Mock<ISsmsConnectionSource>();
            ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S", "D", SqlAuthMode.Windows, null, null, null));

            var vm = new ViewChangesViewModel(
                config.Object, tracker.Object, git.Object, Mock.Of<ISmoManager>(), Mock.Of<IUserNotifier>(),
                Mock.Of<IFileSaveDialog>(), Mock.Of<IWorkingTreeCleaner>(), Mock.Of<IRepositoryConnectDialog>(),
                Mock.Of<ISqlCredentialStore>(), ssms.Object);
            vm.ConnectCommand.Execute(null);

            // 원격 확인은 수동 버튼으로만 돌므로, RemoteStatusLabel을 채우려면 여기서 직접 눌러야 한다.
            if (remoteStatus != null)
            {
                vm.CheckRemoteCommand.Execute(null);
            }

            return new ViewChangesControl(vm, null);
        }
    }
}
#endif
