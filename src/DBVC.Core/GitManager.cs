using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBVC.Core.Models;
using LibGit2Sharp;

namespace DBVC.Core
{
    /// <summary>
    /// <c>LibGit2Sharp</c>를 사용해 로컬 Git 저장소를 제어한다. 외부 git CLI에 의존하지 않는다.
    /// </summary>
    public class GitManager : IGitManager
    {
        private const string DefaultAuthorName = "DBVC User";
        private const string DefaultAuthorEmail = "dbvc@example.com";

        private readonly IConfigManager? _configManager;

        public GitManager()
        {
        }

        public GitManager(IConfigManager configManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        /// <summary>
        /// 해당 경로가 유효한 Git 저장소인지 확인한다. 매핑 등록 전 검증에 쓴다.
        /// </summary>
        public bool IsRepository(string path) => IsValidRepository(path);

        /// <summary>
        /// 저장소의 작업 트리 상태를 요약한다.
        /// 유효한 Git 저장소가 아니면 <c>"Unknown"</c>을 반환한다.
        /// </summary>
        public string GetStatus(string repoPath)
        {
            if (!IsValidRepository(repoPath)) return "Unknown";

            try
            {
                using var repo = new Repository(repoPath);
                return repo.RetrieveStatus(UntrackedInclusiveOptions).IsDirty ? "Modified" : "Clean";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetStatus failed for '{repoPath}': {ex.Message}");
                return "Unknown";
            }
        }

        public string GetStatusForDatabase(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            return repoPath == null ? "Unknown" : GetStatus(repoPath);
        }

        /// <summary>
        /// 작업 트리에서 변경된(수정/추가/삭제/미추적) 파일의 저장소 상대 경로를 반환한다.
        /// 경로 구분자는 Git 규약대로 슬래시('/')이다.
        /// </summary>
        public IReadOnlyList<string> GetChangedFiles(string repoPath)
        {
            if (!IsValidRepository(repoPath)) return new List<string>();

            try
            {
                using var repo = new Repository(repoPath);
                return repo.RetrieveStatus(UntrackedInclusiveOptions)
                    .Where(entry => entry.State != FileStatus.Ignored && entry.State != FileStatus.Unaltered)
                    .Select(entry => entry.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetChangedFiles failed for '{repoPath}': {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 변경된 파일을 상대 경로 → 상태(<c>Added</c>/<c>Modified</c>/<c>Deleted</c>)로 반환한다.
        /// StateTracker가 DDL 로그와 종합해 최종 상태를 결정하는 데 사용한다. (설계 3.3)
        /// </summary>
        public IReadOnlyDictionary<string, string> GetChangedFileStates(string repoPath)
        {
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!IsValidRepository(repoPath)) return states;

            try
            {
                using var repo = new Repository(repoPath);
                foreach (var entry in repo.RetrieveStatus(UntrackedInclusiveOptions))
                {
                    var state = MapFileStatus(entry.State);
                    if (state != null)
                    {
                        states[entry.FilePath] = state;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetChangedFileStates failed for '{repoPath}': {ex.Message}");
            }

            return states;
        }

        public IReadOnlyDictionary<string, string> GetChangedFileStatesForDatabase(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            return repoPath == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : GetChangedFileStates(repoPath);
        }

        private static string? MapFileStatus(FileStatus status)
        {
            if (status == FileStatus.Ignored || status == FileStatus.Unaltered) return null;

            if (status.HasFlag(FileStatus.NewInIndex) || status.HasFlag(FileStatus.NewInWorkdir)) return "Added";
            if (status.HasFlag(FileStatus.DeletedFromIndex) || status.HasFlag(FileStatus.DeletedFromWorkdir)) return "Deleted";
            if (status.HasFlag(FileStatus.ModifiedInIndex) || status.HasFlag(FileStatus.ModifiedInWorkdir)
                || status.HasFlag(FileStatus.RenamedInIndex) || status.HasFlag(FileStatus.RenamedInWorkdir)) return "Modified";

            return null;
        }

        public IReadOnlyList<string> GetChangedFilesForDatabase(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            return repoPath == null ? new List<string>() : GetChangedFiles(repoPath);
        }

        /// <summary>
        /// 변경사항을 스테이징하고 커밋한다.
        /// </summary>
        /// <param name="relativePaths">
        /// 커밋할 파일의 저장소 상대 경로. <c>null</c>이면 모든 변경을 스테이징한다.
        /// </param>
        /// <returns>커밋이 생성되면 true, 매핑이 없거나 스테이징할 변경이 없으면 false.</returns>
        public bool CommitChanges(string serverName, string databaseName, string message, IEnumerable<string>? relativePaths = null)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return false;

            using var repo = new Repository(repoPath);

            var paths = relativePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paths == null)
            {
                Commands.Stage(repo, "*");
            }
            else
            {
                if (paths.Count == 0) return false;
                Commands.Stage(repo, paths);
            }

            if (!HasStagedChanges(repo))
            {
                // 빈 커밋은 LibGit2Sharp에서 EmptyCommitException을 던진다.
                // UI에서 예외로 노출할 일이 아니므로 false로 알린다.
                return false;
            }

            var signature = BuildSignature(repo);
            repo.Commit(message ?? string.Empty, signature, signature);
            return true;
        }

        /// <summary>
        /// 원격 저장소의 변경을 병합한다.
        /// 병합 중 충돌하면 병합을 되돌리고 <see cref="MergeConflictException"/>을,
        /// 겹치는 미커밋 변경으로 병합이 시작조차 못 하면 <see cref="WorkingTreeConflictException"/>을,
        /// 원격이 사용자 자격 증명을 요구하면 <see cref="GitAuthenticationException"/>을 던진다.
        /// </summary>
        public bool PullChanges(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return false;

            using var repo = new Repository(repoPath);

            if (!repo.Network.Remotes.Any())
            {
                throw new InvalidOperationException($"'{repoPath}' 저장소에 원격(remote)이 설정되어 있지 않아 Pull할 수 없습니다.");
            }

            var headBefore = repo.Head.Tip;
            var signature = BuildSignature(repo);

            // 핸들러가 "이 원격은 사용자 자격 증명을 요구한다"고 알려 주면 여기에 기록된다.
            var requiresUserCredentials = false;
            var options = new PullOptions
            {
                FetchOptions = new FetchOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) =>
                    {
                        var credentials = ResolveCredentials(types, out var needsUserCredentials);
                        if (needsUserCredentials) requiresUserCredentials = true;
                        return credentials;
                    }
                }
            };

            MergeResult result;
            try
            {
                result = Commands.Pull(repo, signature, options);
            }
            // CheckoutConflictException은 LibGit2SharpException의 파생 타입이다. 반드시 먼저 잡는다.
            catch (CheckoutConflictException ex)
            {
                // 병합 체크아웃이 시작조차 거부된 상태다. AbortMerge를 부르면 안 된다 - 되돌릴 것이 없고,
                // hard reset은 오히려 사용자의 미커밋 변경을 지운다.
                throw new WorkingTreeConflictException(
                    $"'{repoPath}' 저장소에 받아올 변경과 겹치는 미커밋 변경이 있어 Pull하지 않았습니다. " +
                    "저장소는 변경되지 않았습니다. 해당 변경을 커밋하거나 되돌린 뒤 다시 시도하세요.", ex);
            }
            catch (LibGit2SharpException ex) when (requiresUserCredentials)
            {
                throw new GitAuthenticationException(
                    $"'{repoPath}' 저장소의 원격이 사용자 자격 증명을 요구합니다. " +
                    "DBVC는 Windows 통합 인증만 지원하므로, SSH 키를 사용하거나 " +
                    "원격 URL에 액세스 토큰을 포함해 다시 시도하세요.", ex);
            }

            if (result.Status == MergeStatus.Conflicts)
            {
                AbortMerge(repo, headBefore);
                throw new MergeConflictException(
                    $"'{repoPath}' 저장소에서 Pull 중 병합 충돌이 발생하여 Pull을 중단했습니다. " +
                    "Git 클라이언트에서 충돌을 해결한 뒤 다시 시도하세요.");
            }

            return true;
        }

        /// <summary>
        /// 원격이 요구하는 자격 증명 종류를 보고 무엇을 넘길지 정한다.
        /// DBVC는 Windows 통합 인증(NTLM/Kerberos)만 지원하므로, 그 외를 요구하는 원격은
        /// <paramref name="requiresUserCredentials"/>로 표시만 하고 실패하게 둔다.
        /// libgit2의 인증 오류 메시지는 버전·전송 방식에 따라 달라져 문자열로 매칭할 수 없다.
        /// 대신 핸들러가 호출되는 시점에 원인을 기록해 두고, 예외를 감쌀 때 그 기록을 쓴다.
        /// </summary>
        internal static Credentials ResolveCredentials(
            SupportedCredentialTypes types,
            out bool requiresUserCredentials)
        {
            requiresUserCredentials = !types.HasFlag(SupportedCredentialTypes.Default);
            return new DefaultCredentials();
        }

        /// <summary>
        /// 특정 파일의 커밋 이력을 최신순으로 반환한다. (설계 3.2 History)
        /// </summary>
        public IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string relativeFilePath)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || string.IsNullOrWhiteSpace(relativeFilePath)) return new List<CommitInfo>();

            try
            {
                using var repo = new Repository(repoPath);
                return repo.Commits
                    .QueryBy(NormalizePath(relativeFilePath))
                    .Select(entry => new CommitInfo
                    {
                        Sha = entry.Commit.Sha,
                        Message = entry.Commit.Message,
                        Author = entry.Commit.Author.Name,
                        Date = entry.Commit.Author.When
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetHistory failed for '{relativeFilePath}': {ex.Message}");
                return new List<CommitInfo>();
            }
        }

        /// <summary>
        /// HEAD 시점의 파일 내용을 반환한다. 저장소에 없는 신규 객체면 <c>null</c>을 반환한다.
        /// </summary>
        public string? GetFileContentAtHead(string serverName, string databaseName, string relativeFilePath)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || string.IsNullOrWhiteSpace(relativeFilePath)) return null;

            try
            {
                using var repo = new Repository(repoPath);
                var tip = repo.Head.Tip;
                if (tip == null) return null;

                var entry = tip[NormalizePath(relativeFilePath)];
                if (entry?.Target is Blob blob)
                {
                    return blob.GetContentText();
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetFileContentAtHead failed for '{relativeFilePath}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 파일을 마지막으로 수정한 커밋의 **직전** 내용을 반환한다. (Rollback Script, Feature 9)
        /// 이력에 한 번만 등장하는(= 이후 수정이 없는) 파일은 되돌릴 상태가 없으므로 <c>null</c>이다.
        /// </summary>
        public string? GetFileContentBeforeLastCommit(string serverName, string databaseName, string relativeFilePath)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || string.IsNullOrWhiteSpace(relativeFilePath)) return null;

            try
            {
                using var repo = new Repository(repoPath);
                var path = NormalizePath(relativeFilePath);

                if (repo.Head.Tip?[path] != null)
                {
                    // 아직 존재하는 객체: QueryBy가 해당 경로를 변경한 커밋을 최신순으로 준다.
                    // [0]이 마지막 변경이므로 [1]이 그 직전 상태다.
                    var previous = repo.Commits.QueryBy(path).Skip(1).FirstOrDefault();
                    return previous == null ? null : ReadBlobText(previous.Commit, path);
                }

                // 이미 삭제된 객체: QueryBy는 HEAD에 없는 경로에 대해 빈 결과를 주므로
                // 커밋을 최신순으로 거슬러 파일이 마지막으로 존재했던 시점의 내용을 찾는다.
                return repo.Commits
                    .Select(commit => ReadBlobText(commit, path))
                    .FirstOrDefault(text => text != null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetFileContentBeforeLastCommit failed for '{relativeFilePath}': {ex.Message}");
                return null;
            }
        }

        private static string? ReadBlobText(Commit commit, string path)
        {
            var entry = commit?[path];
            return entry?.Target is Blob blob ? blob.GetContentText() : null;
        }

        private static StatusOptions UntrackedInclusiveOptions => new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        };

        private string? ResolveRepoPath(string serverName, string databaseName)
        {
            if (_configManager == null) return null;
            var repoPath = _configManager.GetMapping(serverName, databaseName);
            return string.IsNullOrWhiteSpace(repoPath) ? null : repoPath;
        }

        private static bool IsValidRepository(string repoPath)
        {
            return !string.IsNullOrWhiteSpace(repoPath)
                && Directory.Exists(repoPath)
                && Repository.IsValid(repoPath);
        }

        private static bool HasStagedChanges(Repository repo)
        {
            var headTree = repo.Head.Tip?.Tree;
            using var changes = repo.Diff.Compare<TreeChanges>(headTree, DiffTargets.Index);
            return changes.Any();
        }

        private static void AbortMerge(Repository repo, Commit? headBefore)
        {
            // Hard reset이 인덱스의 충돌 항목과 병합 진행 상태를 함께 정리한다.
            // 미추적 파일은 건드리지 않는다. 아직 커밋되지 않은 SMO 추출물이 있을 수 있다.
            if (headBefore != null)
            {
                repo.Reset(ResetMode.Hard, headBefore);
            }
        }

        private static Signature BuildSignature(Repository repo)
        {
            // 사용자의 git config를 우선 사용하고, 없을 때만 DBVC 기본값으로 대체한다.
            return repo.Config.BuildSignature(DateTimeOffset.Now)
                ?? new Signature(DefaultAuthorName, DefaultAuthorEmail, DateTimeOffset.Now);
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/');
        }
    }
}
