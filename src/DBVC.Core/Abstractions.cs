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

    /// <summary>
    /// (서버, 데이터베이스)별 SQL 접속 인증 정보를 보관한다.
    /// 매핑과 수명이 다르므로 <see cref="IConfigManager"/>와 분리되어 있다.
    /// </summary>
    public interface ISqlCredentialStore
    {
        /// <summary>이 플랫폼에서 암호를 안전하게 저장할 수 있는지.</summary>
        bool CanPersistPasswords { get; }

        SqlCredential? TryGet(string serverName, string databaseName);
        bool Save(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? plainPassword);
        bool Remove(string serverName, string databaseName);
        string? ResolvePassword(SqlCredential? credential);
    }

    public interface IStateTracker
    {
        bool IsInitialized(string serverName, string databaseName);
        void InitializeDatabase(string serverName, string databaseName);

        /// <summary>접속을 시도해 성공하면 <c>null</c>, 실패하면 사용자에게 보일 한국어 사유.</summary>
        string? TestConnection(string serverName, string databaseName);
        bool RefreshState(string serverName, string databaseName);
        IReadOnlyList<ChangeRecord> GetPendingChanges(string serverName, string databaseName);
        string GetObjectState(string serverName, string databaseName, string objectName);
        void MarkProcessed(string serverName, string databaseName, IEnumerable<ChangeRecord> records);
    }

    public interface IGitManager
    {
        bool IsRepository(string path);
        string GetStatus(string repoPath);
        string GetStatusForDatabase(string serverName, string databaseName);
        IReadOnlyList<string> GetChangedFiles(string repoPath);
        IReadOnlyDictionary<string, string> GetChangedFileStates(string repoPath);
        bool CommitChanges(string serverName, string databaseName, string message, IEnumerable<string>? relativePaths = null);
        bool PullChanges(string serverName, string databaseName);
        IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string relativeFilePath);
        string? GetFileContentAtHead(string serverName, string databaseName, string relativeFilePath);
        string? GetFileContentBeforeLastCommit(string serverName, string databaseName, string relativeFilePath);
    }

    public interface ISmoManager
    {
        bool ScriptObjects(string serverName, string databaseName, List<string>? objectNames = null);
        ScriptResult? ScriptObjectsDetailed(string serverName, string databaseName, List<string>? objectNames = null);
    }

    /// <summary>
    /// 작업 트리를 데이터베이스의 현재 상태에 맞춘다.
    /// DROP된 객체의 파일이 남아 있으면 Git이 삭제를 감지하지 못한다.
    /// </summary>
    public interface IWorkingTreeCleaner
    {
        CleanupResult RemoveDeletedObjectFiles(string repoPath, IEnumerable<ChangeRecord> records);
    }
}
