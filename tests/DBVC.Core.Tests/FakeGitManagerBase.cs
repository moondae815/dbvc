using System;
using System.Collections.Generic;
using System.Threading;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// <see cref="IGitManager"/>의 테스트 대역 뼈대. 부르지 않을 메서드는 던진다 —
    /// 조용히 기본값을 돌려주면 잘못된 경로를 탄 테스트가 초록으로 남는다.
    /// </summary>
    internal abstract class FakeGitManagerBase : IGitManager
    {
        public virtual bool IsRepository(string path) => throw new NotSupportedException();
        public virtual string CloneRepository(string remoteUrl, string targetPath, IProgress<CloneProgress>? progress, CancellationToken cancellationToken, string? branchName = null) => throw new NotSupportedException();
        public virtual RepositoryState? GetRepositoryState(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual string GetStatus(string repoPath) => throw new NotSupportedException();
        public virtual string GetStatusForDatabase(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual IReadOnlyList<string> GetChangedFiles(string repoPath) => throw new NotSupportedException();
        public virtual IReadOnlyDictionary<string, string> GetChangedFileStates(string repoPath) => throw new NotSupportedException();
        public virtual GitCommitResult CommitChanges(string serverName, string databaseName, string message, IEnumerable<string>? relativePaths = null) => throw new NotSupportedException();
        public virtual DiscardResult DiscardChanges(string serverName, string databaseName, IEnumerable<string> relativePaths) => throw new NotSupportedException();
        public virtual PullResult PullChanges(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual IReadOnlyList<BranchInfo> GetBranches(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual BranchResult CreateBranch(string serverName, string databaseName, string branchName) => throw new NotSupportedException();
        public virtual BranchResult SwitchBranch(string serverName, string databaseName, string branchName) => throw new NotSupportedException();
        public virtual PushResult PushChanges(string serverName, string databaseName, bool setUpstream = false) => throw new NotSupportedException();
        public virtual bool HasCommitsToPush(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual RemoteStatus FetchRemoteStatus(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual IReadOnlyList<UnmergedBranch> GetUnmergedBranches(string serverName, string databaseName) => throw new NotSupportedException();
        public virtual IReadOnlyList<DiffFileChange> GetUnifiedDiff(string serverName, string databaseName, IEnumerable<string> relativePaths) => throw new NotSupportedException();
        public virtual IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string? relativeFilePath) => throw new NotSupportedException();
        public virtual string? GetFileContentAtHead(string serverName, string databaseName, string relativeFilePath) => throw new NotSupportedException();
        public virtual string? GetFileContentBeforeLastCommit(string serverName, string databaseName, string relativeFilePath) => throw new NotSupportedException();
        public virtual CommitDetail GetCommitDetail(string serverName, string databaseName, string commitSha, string? relativeFilePath) => throw new NotSupportedException();
    }
}
