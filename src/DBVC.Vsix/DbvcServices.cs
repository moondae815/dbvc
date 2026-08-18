using System;
using DBVC.Core;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix
{
    /// <summary>
    /// DBVC 코어 매니저들의 수명을 관리하는 컨테이너.
    /// VS 셸에 의존하지 않으므로 단위 테스트에서 그대로 사용할 수 있다.
    /// </summary>
    public class DbvcServices
    {
        public IConfigManager ConfigManager { get; }
        public ISqlCredentialStore CredentialStore { get; }
        public IGitManager GitManager { get; }
        public ISmoManager SmoManager { get; }
        public IStateTracker StateTracker { get; }

        /// <summary>
        /// 도구 창의 무거운 작업을 UI 스레드 밖으로 내보내는 구현.
        /// 여기가 인라인이면 새로고침이 다시 SSMS를 붙잡는다.
        /// </summary>
        public IBackgroundScheduler BackgroundScheduler { get; }

        /// <summary>
        /// 확장 전체가 공유하는 인스턴스.
        /// 도구 창과 패키지가 각자 <see cref="ConfigManager"/>를 만들면 같은 mappings.json에
        /// 서로 다른 메모리 상태를 쓰게 되므로 하나만 둔다.
        /// </summary>
        public static DbvcServices Default { get; } = new DbvcServices();

        public DbvcServices() : this(new ConfigManager())
        {
        }

        /// <summary>
        /// 하나의 <see cref="ConfigManager"/>와 <see cref="SessionCredentialStore"/>를 모든 매니저가
        /// 공유하도록 구성한다.
        ///
        /// 인증 저장소를 공유하지 않으면 다른 인스턴스에는 인증 정보가 <b>아예 없다</b> —
        /// 디스크 파일이 있던 시절에는 각자 같은 파일을 읽어 최악의 경우 값이 오래된 정도였지만,
        /// 이제는 메모리뿐이다. ViewModel이 Connect에서 넣은 암호를 StateTracker가 보지 못하면
        /// SQL 인증 접속이 Windows 인증으로 흘러가 실패한다.
        /// </summary>
        public DbvcServices(
            IConfigManager configManager,
            ISqlCredentialStore? credentialStore = null,
            IBackgroundScheduler? backgroundScheduler = null)
        {
            ConfigManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            CredentialStore = credentialStore ?? new SessionCredentialStore();
            BackgroundScheduler = backgroundScheduler ?? new VsBackgroundScheduler();

            var git = new GitManager(ConfigManager);
            GitManager = git;
            SmoManager = new SmoManager(ConfigManager, CredentialStore);
            StateTracker = new StateTracker(ConfigManager, git, CredentialStore);
        }

        public DbvcServices(IConfigManager configManager, IGitManager gitManager, ISmoManager smoManager, IStateTracker stateTracker)
            : this(configManager, gitManager, smoManager, stateTracker, null)
        {
        }

        public DbvcServices(
            IConfigManager configManager,
            IGitManager gitManager,
            ISmoManager smoManager,
            IStateTracker stateTracker,
            ISqlCredentialStore? credentialStore,
            IBackgroundScheduler? backgroundScheduler = null)
        {
            ConfigManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            GitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            SmoManager = smoManager ?? throw new ArgumentNullException(nameof(smoManager));
            StateTracker = stateTracker ?? throw new ArgumentNullException(nameof(stateTracker));
            CredentialStore = credentialStore ?? new SessionCredentialStore();
            BackgroundScheduler = backgroundScheduler ?? new VsBackgroundScheduler();
        }

        private ViewChangesViewModel? _sharedViewModel;

        /// <summary>
        /// 도구 창이 표시 중인 ViewModel. SQL 에디터 컨텍스트 메뉴처럼
        /// 창 밖에서 목록을 조작해야 하는 명령이 같은 인스턴스를 봐야 한다.
        /// </summary>
        public ViewChangesViewModel SharedViewChangesViewModel => _sharedViewModel ??= CreateViewChangesViewModel();

        /// <summary>
        /// 도구 창이 쓸 ViewModel을 만든다. SSMS 개체 탐색기 연동은 기본으로 켠다 —
        /// 셸 밖에서는 어댑터가 <c>null</c>을 돌려줄 뿐이므로 안전하다.
        /// </summary>
        public ViewChangesViewModel CreateViewChangesViewModel(
            IUserNotifier? notifier = null,
            ISsmsConnectionSource? ssmsConnectionSource = null)
        {
            return new ViewChangesViewModel(
                ConfigManager, StateTracker, GitManager, SmoManager, notifier,
                credentialStore: CredentialStore,
                ssmsConnectionSource: ssmsConnectionSource ?? new ObjectExplorerConnectionSource(),
                scheduler: BackgroundScheduler);
        }

        public DiffService CreateDiffService()
        {
            return new DiffService(ConfigManager, GitManager);
        }
    }
}
