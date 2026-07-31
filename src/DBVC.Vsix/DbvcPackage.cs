using System;
using DBVC.Core;

namespace DBVC.Vsix
{
    public class DbvcPackage
    {
        public ConfigManager ConfigManager { get; }
        public GitManager GitManager { get; }
        public SmoManager SmoManager { get; }
        public StateTracker StateTracker { get; }

        public DbvcPackage()
        {
            ConfigManager = new ConfigManager();
            GitManager = new GitManager(ConfigManager);
            SmoManager = new SmoManager();
            StateTracker = new StateTracker();
        }

        public DbvcPackage(ConfigManager configManager, GitManager gitManager, SmoManager smoManager, StateTracker stateTracker)
        {
            ConfigManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            GitManager = gitManager ?? throw new ArgumentNullException(nameof(gitManager));
            SmoManager = smoManager ?? throw new ArgumentNullException(nameof(smoManager));
            StateTracker = stateTracker ?? throw new ArgumentNullException(nameof(stateTracker));
        }
    }
}
