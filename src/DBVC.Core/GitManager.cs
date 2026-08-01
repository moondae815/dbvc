using System;
using LibGit2Sharp;

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
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
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
            string? repoPath = _configManager.GetMapping(serverName, databaseName);
            if (string.IsNullOrEmpty(repoPath)) return "Clean";
            return GetStatus(repoPath!);
        }

        public bool Commit(string repoPath, string filePath, string message)
        {
            return true;
        }

        public bool CommitChanges(string serverName, string databaseName, string message)
        {
            if (_configManager == null) return false;
            var repoPath = _configManager.GetMapping(serverName, databaseName);
            if (string.IsNullOrEmpty(repoPath)) return false;

            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                
                var signature = new Signature("DBVC User", "dbvc@example.com", DateTimeOffset.Now);
                repo.Commit(message, signature, signature);
            }

            return true;
        }

        public bool PullChanges(string serverName, string databaseName)
        {
            // Stubbed for now
            return true;
        }
    }
}
