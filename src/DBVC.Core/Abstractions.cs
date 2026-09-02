using System;
using System.Collections.Generic;
using System.Threading;
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

        /// <summary>Mode·Branch까지 함께 저장한다. 저장소 연결 대화상자가 이 오버로드로만 용도를 담는다.</summary>
        void AddMapping(MappingConfig mapping);
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
        /// <summary>설치된 스키마 버전. 0이면 미설치다.</summary>
        int GetInstalledVersion(string serverName, string databaseName);
        void InitializeDatabase(string serverName, string databaseName);

        /// <summary>접속을 시도해 성공하면 <c>null</c>, 실패하면 사용자에게 보일 한국어 사유.</summary>
        string? TestConnection(string serverName, string databaseName);
        bool RefreshState(string serverName, string databaseName);

        /// <param name="includeAllAuthors">
        /// true면 다른 사람이 만든 변경까지 읽는다. 기본 화면은 false다 —
        /// 공용 계정 환경에서 필터가 없으면 목록에 남의 진행 중 작업이 전부 뜨고,
        /// 전체 선택 커밋 한 번이면 검증되지 않은 남의 작업이 브랜치에 담긴다.
        /// </param>
        bool RefreshState(string serverName, string databaseName, bool includeAllAuthors);

        /// <summary>
        /// 커밋하려는 객체들을 다른 작업자도 만졌는지 조회한다. 비어 있으면 경고할 것이 없다.
        /// 화면 필터와 무관하게 항상 전체 로그를 본다 - "내 변경만" 상태에서도 남이 만졌다는
        /// 사실은 알려야 한다.
        /// </summary>
        IReadOnlyList<CoAuthorWarning> GetCoAuthorWarnings(
            string serverName, string databaseName, IEnumerable<string> qualifiedNames);

        /// <summary>아직 처리되지 않은 DDL 로그가 가리키는 객체의 스키마 한정 이름.</summary>
        IReadOnlyList<string> GetChangedObjectNames(string serverName, string databaseName);
        IReadOnlyList<ChangeRecord> GetPendingChanges(string serverName, string databaseName);
        string GetObjectState(string serverName, string databaseName, string objectName);
        void MarkProcessed(string serverName, string databaseName, IEnumerable<ChangeRecord> records);
    }

    public interface IGitManager
    {
        bool IsRepository(string path);

        /// <summary>
        /// 원격 저장소를 <paramref name="targetPath"/>에 받고 그 작업 트리 경로를 반환한다.
        ///
        /// 매핑이 생기기 전에 일어나므로 다른 API와 달리 (serverName, databaseName)을 받지 않는다.
        /// <paramref name="targetPath"/>는 <b>없는 폴더</b>여야 한다 — 이 제약이 있어야
        /// 실패·취소 뒤처리에서 "지워도 되는 폴더"를 판별할 필요가 없다.
        /// </summary>
        /// <param name="cancellationToken">
        /// 취소되면 <see cref="OperationCanceledException"/>이 전파되고 만든 폴더는 지워진다.
        /// 다만 취소가 즉시 걸리는 것은 받는 동안뿐이다 — libgit2의 checkout 콜백은 중단을 받지 않는다.
        /// </param>
        /// <param name="branchName">
        /// 지정하면 원격 HEAD 대신 이 브랜치를 체크아웃한다. 배포·감사 클론이 쓴다 — 원격 HEAD를
        /// 받아 두면 받자마자 브랜치 불일치로 차단되어 사용자가 외부 클라이언트를 다시 꺼내야 한다.
        /// </param>
        string CloneRepository(
            string remoteUrl,
            string targetPath,
            IProgress<CloneProgress>? progress,
            CancellationToken cancellationToken,
            string? branchName = null);

        /// <summary>
        /// 저장소를 그대로 써도 되는지 판정한 결과. 매핑이 없으면 null이다.
        ///
        /// DBVC는 저장소의 유일한 주인이 아니다 — 외부 Git 클라이언트가 남긴 상태를 만나는 것이
        /// 정상이고, 만나면 멈춰야 한다. 판정 자체는 RepositoryStateEvaluator에 있다.
        /// </summary>
        RepositoryState? GetRepositoryState(string serverName, string databaseName);
        string GetStatus(string repoPath);
        string GetStatusForDatabase(string serverName, string databaseName);
        IReadOnlyList<string> GetChangedFiles(string repoPath);
        IReadOnlyDictionary<string, string> GetChangedFileStates(string repoPath);
        GitCommitResult CommitChanges(string serverName, string databaseName, string message, IEnumerable<string>? relativePaths = null);
        PullResult PullChanges(string serverName, string databaseName);
        PushResult PushChanges(string serverName, string databaseName);
        bool HasCommitsToPush(string serverName, string databaseName);

        /// <summary>
        /// 원격을 받아 앞섬·뒤처짐을 센다. 참조만 갱신하고 작업 트리는 건드리지 않는다.
        /// 매핑이 없거나 원격·추적 브랜치가 없으면 한국어 안내를 담은 예외를 던진다.
        /// </summary>
        RemoteStatus FetchRemoteStatus(string serverName, string databaseName);
        /// <summary><paramref name="relativeFilePath"/>가 비면 저장소 전체 이력을 반환한다.</summary>
        IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string? relativeFilePath);
        string? GetFileContentAtHead(string serverName, string databaseName, string relativeFilePath);
        string? GetFileContentBeforeLastCommit(string serverName, string databaseName, string relativeFilePath);
        string? GetFileContentAtCommit(string serverName, string databaseName, string relativeFilePath, string commitSha);
        string? GetFileContentAtCommitParent(string serverName, string databaseName, string relativeFilePath, string commitSha);
        IReadOnlyList<HistoryChangedFile> GetChangedFilesAtCommit(string serverName, string databaseName, string commitSha);

        /// <summary>
        /// 커밋 하나의 정보를 한 번의 저장소 열기로 읽는다.
        /// <paramref name="relativeFilePath"/>가 비면 변경 파일 목록만, 주어지면 그 파일의 이전·이후 본문만 채운다.
        /// </summary>
        CommitDetail GetCommitDetail(string serverName, string databaseName, string commitSha, string? relativeFilePath);
    }

    public interface ISmoManager
    {
        bool ScriptObjects(string serverName, string databaseName, List<string>? objectNames = null);
        /// <param name="progress">객체 하나를 처리할 때마다 보고한다. 최초 온보딩은 길다.</param>
        /// <param name="cancellationToken">
        /// 취소되면 <see cref="OperationCanceledException"/>이 전파된다.
        /// 이미 추출해 둔 파일은 그대로 남는다 — 취소는 되돌리기가 아니다.
        /// </param>
        ScriptResult? ScriptObjectsDetailed(
            string serverName,
            string databaseName,
            List<string>? objectNames = null,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 대상 DB와 저장소 작업 트리의 차이를 판정한다. <b>저장소에 아무것도 쓰지 않는다</b> —
        /// 그래서 실패하거나 취소해도 되돌릴 것이 없다.
        ///
        /// 매핑이 없거나 접속에 실패하면 <c>null</c>이다. mode가 허용하지 않으면
        /// <see cref="OperationNotAllowedException"/>을 던진다.
        /// </summary>
        ComparisonResult? CompareWithRepository(
            string serverName,
            string databaseName,
            IProgress<ExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 객체 하나의 현재 DDL을 텍스트로 읽는다. 저장소에 쓰지 않는다.
        /// 대상에 없거나 스크립팅에 실패하면 <c>null</c>이다.
        /// </summary>
        string? ScriptObjectToText(string serverName, string databaseName, string qualifiedName);
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
