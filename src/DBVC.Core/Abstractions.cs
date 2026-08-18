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
    /// (서버, 데이터베이스)별 SQL 접속 인증 정보를 이 프로세스가 사는 동안만 보관한다.
    ///
    /// 디스크에 쓰지 않는다 — 값의 출처는 SSMS 개체 탐색기뿐이고, SSMS가 닫히면 함께 사라진다.
    /// 매핑(<see cref="IConfigManager"/>)과는 수명도 저장 매체도 다르므로 분리되어 있다.
    /// </summary>
    public interface ISqlCredentialStore
    {
        SqlCredential? TryGet(string serverName, string databaseName);

        /// <summary>
        /// 이 대상의 인증 정보를 통째로 덮어쓴다. 이전 값과 병합하지 않는다.
        /// </summary>
        void Set(string serverName, string databaseName, SqlAuthMode authMode, string? userName, string? password);
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
        PushResult PushChanges(string serverName, string databaseName);
        /// <summary><paramref name="relativeFilePath"/>가 비면 저장소 전체 이력을 반환한다.</summary>
        IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string? relativeFilePath);
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
