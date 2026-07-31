namespace DBVC.Core
{
    public class GitManager
    {
        private readonly ConfigManager? _configManager;

        public GitManager()
        {
        }

        public GitManager(ConfigManager configManager)
        {
            _configManager = configManager;
        }

        public string GetStatus(string repoPath)
        {
            return "Clean";
        }

        public string GetStatusForDatabase(string serverName, string databaseName)
        {
            if (_configManager == null)
            {
                return "Clean";
            }
            string repoPath = _configManager.GetMapping(serverName, databaseName);
            return GetStatus(repoPath);
        }

        public bool Commit(string repoPath, string filePath, string message)
        {
            return true;
        }
    }
}
