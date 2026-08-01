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
        public IGitManager GitManager { get; }
        public ISmoManager SmoManager { get; }
        public IStateTracker StateTracker { get; }

        public DbvcServices() : this(new ConfigManager())
        {
        }

        /// <summary>
        /// 하나의 <see cref="ConfigManager"/>를 모든 매니저가 공유하도록 구성한다.
        /// </summary>
        public DbvcServices(IConfigManager configManager)
        {
            ConfigManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

            var git = new GitManager(ConfigManager);
            GitManager = git;
            SmoManager = new SmoManager(ConfigManager);
            StateTracker = new StateTracker(ConfigManager, git);
        }

        public DbvcServices(IConfigManager configManager, IGitManager gitManager, ISmoManager smoManager, IStateTracker stateTracker)
        {
            ConfigManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            GitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            SmoManager = smoManager ?? throw new ArgumentNullException(nameof(smoManager));
            StateTracker = stateTracker ?? throw new ArgumentNullException(nameof(stateTracker));
        }

        public ViewChangesViewModel CreateViewChangesViewModel(IUserNotifier? notifier = null)
        {
            return new ViewChangesViewModel(ConfigManager, StateTracker, GitManager, SmoManager, notifier);
        }

        public DiffService CreateDiffService()
        {
            return new DiffService(ConfigManager, GitManager);
        }
    }
}
