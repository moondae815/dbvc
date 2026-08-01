using System.Collections.Generic;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// UI 계층이 DB나 Git 없이 테스트될 수 있도록 코어 매니저를 추상화한다.
    /// 각 인터페이스는 UI가 실제로 사용하는 연산만 노출한다.
    /// </summary>
    public interface IConfigManager
    {
        MappingConfig? TryGetMapping(string serverName, string databaseName);
        string? GetMapping(string serverName, string databaseName);
        void AddMapping(string serverName, string databaseName, string gitPath);
        bool RemoveMapping(string serverName, string databaseName);
        IReadOnlyList<MappingConfig> GetAllMappings();
    }

    public interface IStateTracker
    {
        bool IsInitialized(string connectionString);
        void InitializeDatabase(string connectionString);
        bool RefreshState(string serverName, string databaseName);
        IReadOnlyList<ChangeRecord> GetPendingChanges(string serverName, string databaseName);
        string GetObjectState(string serverName, string databaseName, string objectName);
        void MarkProcessed(string serverName, string databaseName, IEnumerable<ChangeRecord> records);
    }

    public interface IGitManager
    {
        string GetStatus(string repoPath);
        string GetStatusForDatabase(string serverName, string databaseName);
        IReadOnlyList<string> GetChangedFiles(string repoPath);
        IReadOnlyDictionary<string, string> GetChangedFileStates(string repoPath);
        bool CommitChanges(string serverName, string databaseName, string message, IEnumerable<string>? relativePaths = null);
        bool PullChanges(string serverName, string databaseName);
        IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string relativeFilePath);
        string? GetFileContentAtHead(string serverName, string databaseName, string relativeFilePath);
    }

    public interface ISmoManager
    {
        bool ScriptObjects(string serverName, string databaseName, List<string>? objectNames = null);
        ScriptResult? ScriptObjectsDetailed(string serverName, string databaseName, List<string>? objectNames = null);
    }
}
