using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DBVC.Core.Models;
using LibGit2Sharp;
// LibGit2Sharp도 최상위 PushResult 클래스를 갖고 있어 두 using만으로는 모호하다(CS0104).
// 이 파일의 PushChanges가 반환하는 것은 DBVC의 PushResult이므로 별칭으로 고정한다.
using PushResult = DBVC.Core.Models.PushResult;

namespace DBVC.Core
{
    /// <summary>
    /// <c>LibGit2Sharp</c>를 사용해 로컬 Git 저장소를 제어한다. 외부 git CLI에 의존하지 않는다.
    /// </summary>
    public class GitManager : IGitManager
    {
        private const string DefaultAuthorName = "DBVC User";
        private const string DefaultAuthorEmail = "dbvc@example.com";

        /// <summary>
        /// <see cref="RemoteDiagnostics.Explain"/>이 판정하지 못한 원격에서 자격 증명이 요구된 경우.
        /// 정상 경로에서는 도달하지 않지만, 메시지 없는 예외를 던지지 않도록 둔다.
        /// </summary>
        private const string CredentialFallbackMessage =
            "이 원격의 인증 방식을 DBVC가 처리할 수 없습니다. SSH 원격을 사용하세요.";

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
        /// 원격 저장소를 받는다. (설계 3.11)
        /// </summary>
        public string CloneRepository(
            string remoteUrl,
            string targetPath,
            IProgress<CloneProgress>? progress,
            CancellationToken cancellationToken,
            string? branchName = null)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                throw new ArgumentException("원격 주소가 비어 있습니다.", nameof(remoteUrl));
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("받을 폴더 경로가 비어 있습니다.", nameof(targetPath));
            }

            // HTTPS는 네트워크를 타기 전에 거른다. 자격 증명 콜백까지 흘려보내면 libgit2의
            // 영문 원문이 먼저 나오고, 그 뒤에 붙이는 안내는 이미 늦다.
            if (RemoteDiagnostics.Classify(remoteUrl) == RemoteUrlKind.Https)
            {
                throw new GitAuthenticationException(
                    RemoteDiagnostics.Explain(remoteUrl, IsSshAvailableWithoutRepository())!);
            }

            // 있는 폴더에 받으면 "이 폴더를 내가 만들었나"를 기억해야 하고, 그 기억이 틀리는 날
            // 남의 폴더를 지운다. 없는 폴더만 받으면 그 판별 자체가 필요 없다.
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                throw new InvalidOperationException(
                    $"'{targetPath}'에 이미 무언가 있습니다. 아직 없는 폴더 경로를 지정하세요.");
            }

            var options = new CloneOptions
            {
                OnCheckoutProgress = (path, completed, total) =>
                    progress?.Report(new CloneProgress(ClonePhase.CheckingOut, completed, total))
            };

            // 배포·감사 클론은 특정 브랜치에 고정된다. 원격 HEAD를 받아 두면 받자마자
            // 브랜치 불일치로 차단되어 사용자가 외부 클라이언트를 다시 꺼내야 한다.
            if (!string.IsNullOrWhiteSpace(branchName))
            {
                options.BranchName = branchName;
            }

            // 자격 증명과 전송 진행률은 CloneOptions가 아니라 그 안의 FetchOptions에 있다.
            // new CloneOptions()가 FetchOptions를 이미 채워 주므로 그대로 쓴다(실측 확인).
            options.FetchOptions.OnTransferProgress = transfer =>
            {
                progress?.Report(new CloneProgress(
                    ClonePhase.Transferring, transfer.ReceivedObjects, transfer.TotalObjects));

                // false를 반환하면 libgit2가 전송을 끊고 UserCancelledException을 낸다.
                // 취소가 즉시 걸리는 유일한 자리다.
                return !cancellationToken.IsCancellationRequested;
            };
            options.FetchOptions.CredentialsProvider =
                (url, usernameFromUrl, types) => ResolveCredentials(types, out _);

            // 취소가 이미 걸려 있으면 폴더를 만들지도 않는다.
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Repository.Clone(remoteUrl, targetPath, options);

                // checkout 단계는 libgit2가 중단을 받지 않으므로, 그 사이에 눌린 취소는
                // 여기서 처리한다. 사용자가 취소를 눌렀는데 저장소가 남아 있으면
                // 다음 시도가 '이미 있음'으로 막혀 무엇을 해야 하는지 알 수 없게 된다.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                // 우리가 만든 폴더만 존재할 수 있다(위 가드가 보장한다). 지워도 안전하다.
                DeleteDirectoryTree(targetPath);

                if (ex is OperationCanceledException) throw;

                // 콜백이 false를 반환해 끊긴 경우다. 실패가 아니라 사용자가 누른 것이다.
                if (ex is UserCancelledException)
                {
                    throw new OperationCanceledException("원격 저장소 받기를 취소했습니다.", ex, cancellationToken);
                }

                // 원격 실패가 아닌 것을 원격 실패로 둔갑시키지 않는다. 정리는 이미 했으므로
                // 그대로 흘려보낸다 — 코딩 실수나 디스크 부족에 SSH 안내를 붙이면 원인이 가려진다.
                if (!(ex is LibGit2SharpException)) throw;

                var guidance = RemoteDiagnostics.Explain(remoteUrl, IsSshAvailableWithoutRepository());
                var message = guidance == null
                    ? ex.Message
                    : ex.Message + Environment.NewLine + Environment.NewLine + guidance;

                throw new GitRemoteException(message, ex);
            }

            // Repository.Clone의 반환값은 .git 디렉터리 경로다. 매핑에 들어갈 것은 작업 트리다.
            return targetPath;
        }

        /// <summary>
        /// 저장소가 그대로 써도 되는 상태인지 판정한다. 매핑이 없거나 유효한 저장소가 아니면 null이다.
        /// 값을 읽는 일만 여기서 하고, 차단 여부 판정은 RepositoryStateEvaluator에 맡긴다.
        /// </summary>
        public RepositoryState? GetRepositoryState(string serverName, string databaseName)
        {
            var mapping = _configManager?.TryGetMapping(serverName, databaseName);
            if (mapping == null || !IsValidRepository(mapping.GitPath)) return null;

            using var repo = new Repository(mapping.GitPath);

            // CurrentOperation은 병합·리베이스·체리픽이 끝나지 않았을 때만 None이 아니다.
            var operation = repo.Info.CurrentOperation == CurrentOperation.None
                ? null
                : repo.Info.CurrentOperation.ToString();

            var detached = repo.Info.IsHeadDetached;
            var branch = detached ? null : repo.Head.FriendlyName;

            // write에서는 더러운 트리가 정상이므로 묻지 않는다. RetrieveStatus는 작업 트리
            // 전체를 훑어 객체 수에 비례하는 비용이 있고, 이 함수는 대상을 열 때마다 돈다.
            var dirty = mapping.Mode != MappingMode.Write
                && repo.RetrieveStatus(UntrackedInclusiveOptions).IsDirty;

            var reason = RepositoryStateEvaluator.Evaluate(
                branch, detached, operation, mapping.Branch, mapping.Mode, dirty);

            return new RepositoryState
            {
                CurrentBranch = branch,
                IsDetached = detached,
                PendingOperation = operation,
                BlockReason = reason,
                BlockMessage = RepositoryStateEvaluator.BuildMessage(reason, branch, mapping.Branch, operation)
            };
        }

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
        public GitCommitResult CommitChanges(string serverName, string databaseName, string message, IEnumerable<string>? relativePaths = null)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return GitCommitResult.NotMapped;

            // 테스트 DB에서 나온 추출물은 새 변경이 아니라 배포 결과다. 커밋하면 develop에
            // 자기 자신을 되먹이고, 배포가 덜 된 상태였다면 그것을 정답으로 굳혀 버린다.
            var mapping = _configManager?.TryGetMapping(serverName, databaseName);
            if (mapping != null && !MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.Commit))
            {
                throw new OperationNotAllowedException(mapping.Mode, DbvcOperation.Commit);
            }

            using var repo = new Repository(repoPath);

            var paths = relativePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paths == null)
            {
                Commands.Stage(repo, "*");
            }
            else
            {
                if (paths.Count == 0) return GitCommitResult.NothingSelected;
                Commands.Stage(repo, paths);
            }

            if (!HasStagedChanges(repo))
            {
                // 빈 커밋은 LibGit2Sharp에서 EmptyCommitException을 던진다. 예외로 노출할 일이
                // 아니고, "못 했다"와도 다르다 - 저장소가 이미 DB와 같다는 뜻이라 호출자는
                // 그 객체의 로그 행을 닫아야 한다.
                return GitCommitResult.NothingToCommit;
            }

            var signature = BuildSignature(repo);
            repo.Commit(message ?? string.Empty, signature, signature);
            return GitCommitResult.Committed;
        }

        /// <summary>
        /// 원격 저장소의 변경을 병합한다.
        /// 병합 중 충돌하면 병합을 되돌리고 <see cref="MergeConflictException"/>을,
        /// 겹치는 미커밋 변경으로 병합이 시작조차 못 하면 <see cref="WorkingTreeConflictException"/>을,
        /// 원격이 사용자 자격 증명을 요구하면 <see cref="GitAuthenticationException"/>을,
        /// 그 외에 원격과 통신하지 못했고 안내할 원인이 있으면 <see cref="GitRemoteException"/>을 던진다.
        /// 원격이 없거나 현재 브랜치에 추적 중인 원격 브랜치가 없으면 <see cref="InvalidOperationException"/>을 던진다.
        /// </summary>
        public PullResult PullChanges(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return PullResult.NoMapping;

            using var repo = new Repository(repoPath);

            var guidance = ValidateRemoteAndBuildGuidance(repo, repoPath, "Pull");

            var headBefore = repo.Head.Tip;
            var signature = BuildSignature(repo);

            // 핸들러가 "이 원격은 사용자 자격 증명을 요구한다"고 알려 주면 여기에 기록된다.
            var requiresUserCredentials = false;
            var options = BuildPullOptions(() => requiresUserCredentials = true);

            MergeResult result;
            try
            {
                result = Commands.Pull(repo, signature, options);
            }
            // CheckoutConflictException은 LibGit2SharpException의 파생 타입이므로 반드시 먼저 잡는다.
            // 아래 catch에 when (guidance != null) 필터가 붙은 뒤로는 순서가 정확성 문제다 - 이 catch를
            // 뒤로 옮기면 SSH/HTTPS 원격에서 발생한 체크아웃 충돌이 WorkingTreeConflictException 대신
            // GitRemoteException으로 둔갑하고, 미커밋 변경을 보존한다는 이 catch의 의미를 잃는다.
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
                // 콜백이 호출됐다는 것 자체가 "이 원격은 HTTPS이고 자격 증명을 요구한다"는 신호다.
                // SSH는 시스템 ssh 실행 파일이 처리하므로 이 콜백을 거치지 않는다.
                throw new GitAuthenticationException(
                    $"'{repoPath}' 저장소의 원격이 사용자 자격 증명을 요구합니다." +
                    Environment.NewLine + Environment.NewLine +
                    (guidance ?? CredentialFallbackMessage), ex);
            }
            // 안내할 것이 있을 때만 가로챈다. 없으면 원본 예외가 그대로 전파되어
            // 무관한 libgit2 오류를 엉뚱한 메시지로 삼키지 않는다.
            catch (LibGit2SharpException ex) when (guidance != null)
            {
                throw new GitRemoteException(
                    ex.Message + Environment.NewLine + Environment.NewLine + guidance, ex);
            }

            if (result.Status == MergeStatus.Conflicts)
            {
                AbortMerge(repo, headBefore);
                throw new MergeConflictException(
                    $"'{repoPath}' 저장소에서 Pull 중 병합 충돌이 발생하여 Pull을 중단했습니다. " +
                    "Git 클라이언트에서 충돌을 해결한 뒤 다시 시도하세요.");
            }

            // UpToDate는 "받을 것이 없었다"이지 실패가 아니다. Pulled와 구분하지 않으면
            // 화면이 받은 것이 없는데 받았다고 말하고, 사용자는 받은 스크립트를 찾아 헤맨다.
            return result.Status == MergeStatus.UpToDate
                ? PullResult.AlreadyUpToDate
                : PullResult.Pulled;
        }

        /// <summary>
        /// 로컬 브랜치에 원격 브랜치로 푸시할 커밋이 남아 있는지 확인한다.
        /// 원격이 없거나 추적 브랜치가 설정되지 않은 경우 false를 반환한다.
        /// </summary>
        public bool HasCommitsToPush(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return false;

            try
            {
                using var repo = new Repository(repoPath);

                // 원격이 없거나 추적 중인 브랜치가 없으면 올릴 수 없다(libgit2 예외와 일치).
                if (!repo.Network.Remotes.Any() || !repo.Head.IsTracking) return false;

                return repo.Head.TrackingDetails.AheadBy > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.HasCommitsToPush failed for '{repoPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 원격 상태를 부수효과 없이 읽는다. (설계 3.11)
        ///
        /// 수동 버튼으로만 불린다. 새로고침에 붙이면 응답 없는 원격이 변경 목록을 보는 일까지
        /// 느리게 만든다.
        /// </summary>
        public RemoteStatus FetchRemoteStatus(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null)
            {
                throw new InvalidOperationException(
                    $"'{serverName}.{databaseName}'에 연결된 Git 저장소가 없어 원격을 확인할 수 없습니다.");
            }

            using var repo = new Repository(repoPath);

            // 원격 없음·추적 브랜치 없음의 안내는 Pull·Push가 쓰는 것을 그대로 쓴다.
            var guidance = ValidateRemoteAndBuildGuidance(repo, repoPath, "원격 확인");
            var remoteName = repo.Head.RemoteName;

            try
            {
                var fetchOptions = new FetchOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) => ResolveCredentials(types, out _)
                };

                // 빈 refspec은 "원격에 설정된 기본 refspec을 쓰라"는 뜻이다.
                Commands.Fetch(repo, remoteName, Array.Empty<string>(), fetchOptions, null);
            }
            // Pull·Push와 같은 모양으로 좁힌다. 안내할 것이 있을 때만 가로채고,
            // 없으면 원본 예외를 그대로 흘려보낸다 — 모든 예외를 감싸면 코딩 실수까지
            // "원격과 통신하지 못했다"로 둔갑해서 원인을 찾을 수 없게 된다.
            catch (LibGit2SharpException ex) when (guidance != null)
            {
                throw new GitRemoteException(
                    ex.Message + Environment.NewLine + Environment.NewLine + guidance, ex);
            }

            var details = repo.Head.TrackingDetails;
            return new RemoteStatus(details.AheadBy ?? 0, details.BehindBy ?? 0);
        }

        /// <summary>
        /// 현재 브랜치의 커밋을 추적 중인 원격 브랜치에 올린다.
        /// 원격이 ref 갱신을 거부하면 <see cref="GitPushRejectedException"/>을,
        /// 원격이 사용자 자격 증명을 요구하면 <see cref="GitAuthenticationException"/>을,
        /// 그 외에 원격과 통신하지 못했고 안내할 원인이 있으면 <see cref="GitRemoteException"/>을 던진다.
        /// 원격이 없거나 현재 브랜치에 추적 중인 원격 브랜치가 없으면 <see cref="InvalidOperationException"/>을 던진다.
        ///
        /// 이 메서드는 작업 트리·인덱스·로컬 브랜치 이력을 바꾸지 않는다. 성공하면 원격 추적
        /// ref(<c>refs/remotes/...</c>)만 갱신되고(libgit2의 git_remote_update_tips) - 두 번째
        /// Push가 "올릴 커밋이 없습니다"를 정확히 판정하는 이유이기도 하다 - 실패하면 그마저도
        /// 바뀌지 않는다. 잃을 것이 없으므로 Pull의 AbortMerge에 해당하는 복구 경로가 없다.
        /// </summary>
        public PushResult PushChanges(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return PushResult.NoMapping;

            // 커밋을 막아도 그 전에 만들어진 로컬 커밋이 남아 있을 수 있다 - Push까지 막지
            // 않으면 커밋 차단이 우회로를 하나 남기는 셈이다.
            var mapping = _configManager?.TryGetMapping(serverName, databaseName);
            if (mapping != null && !MappingPolicy.IsAllowed(mapping.Mode, DbvcOperation.Push))
            {
                throw new OperationNotAllowedException(mapping.Mode, DbvcOperation.Push);
            }

            using var repo = new Repository(repoPath);

            var guidance = ValidateRemoteAndBuildGuidance(repo, repoPath, "Push");

            // 마지막 fetch 기준의 로컬 값이다. 0이면 올릴 것이 없는 것이 확실하지만
            // (로컬 커밋은 언제나 이 값을 올린다), 0보다 크다고 해서 원격이 뒤처져 있다는
            // 보장은 없다. 헛수고를 줄이는 검사이지 성공/거부 판정의 근거가 아니다.
            if (repo.Head.TrackingDetails.AheadBy == 0) return PushResult.NothingToPush;

            var requiresUserCredentials = false;
            var pushErrors = new List<PushStatusError>();
            var options = BuildPushOptions(
                () => requiresUserCredentials = true,
                error => pushErrors.Add(error));

            try
            {
                repo.Network.Push(repo.Head, options);
            }
            // NonFastForwardException은 LibGit2SharpException의 파생 타입이므로 반드시 먼저 잡는다.
            // 아래 catch에 when (guidance != null) 필터가 붙어 있어 순서가 곧 정확성이다 - 이 catch를
            // 뒤로 옮기면 SSH 원격에서 발생한 거부가 GitPushRejectedException 대신
            // GitRemoteException으로 둔갑한다. PullChanges가 CheckoutConflictException에서 겪은 함정과 같다.
            catch (NonFastForwardException ex)
            {
                // 드물지만 전송이 상태 오류를 먼저 보고한 뒤 non-fast-forward로 판정할 수도 있다.
                // 그런 경우 서버의 원문을 놓치지 않고 싣는다.
                throw new GitPushRejectedException(
                    BuildPushRejectionMessage(pushErrors.Count > 0 ? pushErrors[0] : null), ex);
            }
            catch (LibGit2SharpException ex) when (requiresUserCredentials)
            {
                // 콜백이 호출됐다는 것 자체가 "이 원격은 HTTPS이고 자격 증명을 요구한다"는 신호다.
                // SSH는 시스템 ssh 실행 파일이 처리하므로 이 콜백을 거치지 않는다.
                throw new GitAuthenticationException(
                    $"'{repoPath}' 저장소의 원격이 사용자 자격 증명을 요구합니다." +
                    Environment.NewLine + Environment.NewLine +
                    (guidance ?? CredentialFallbackMessage), ex);
            }
            catch (LibGit2SharpException ex) when (guidance != null)
            {
                throw new GitRemoteException(
                    ex.Message + Environment.NewLine + Environment.NewLine + guidance, ex);
            }

            // 서버가 상태로 거부를 보고하는 경로다(smart 전송 - SSH·HTTPS).
            // 이 검사가 없으면 Network.Push가 정상 반환한 것을 성공으로 읽게 된다.
            if (pushErrors.Count > 0)
            {
                throw new GitPushRejectedException(BuildPushRejectionMessage(pushErrors[0]));
            }

            return PushResult.Pushed;
        }

        /// <summary>
        /// 거부 안내를 만든다. 서버가 준 원문이 있으면 그대로 싣고, 그 아래 원인 후보를 붙인다.
        ///
        /// 서버·libgit2의 메시지를 문자열로 매칭해 원인을 판정하지 않는다 - 버전과 전송 방식에
        /// 따라 달라진다. 대신 후보를 둘로 한정한다. force push를 제공하지 않는 이상
        /// ref 갱신이 거부되는 원인은 실제로 이 둘뿐이다.
        ///
        /// <c>internal</c>로 노출하는 이유는 <see cref="BuildPushOptions"/>와 같다 - 파일 기반
        /// 전송으로는 <c>OnPushStatusError</c>가 호출되는 상황 자체를 재현할 수 없어(non-bare
        /// 대상은 상태 오류 없이 BareRepositoryException을 던진다), 이 문구 조립을 검증할
        /// 유일한 방법이 <see cref="PushStatusError"/>의 테스트 이중체로 직접 호출하는 것이다.
        /// </summary>
        internal static string BuildPushRejectionMessage(PushStatusError? error)
        {
            var header = error == null
                ? "원격이 Push를 거부했습니다."
                : $"원격이 '{error.Reference}' 갱신을 거부했습니다." +
                  Environment.NewLine + $"서버 응답: {error.Message}";

            return header + Environment.NewLine + Environment.NewLine +
                "원인은 보통 둘 중 하나입니다." + Environment.NewLine +
                "- 원격에 로컬로 가져오지 않은 커밋이 있습니다. Pull을 먼저 하세요." + Environment.NewLine +
                "- 이 브랜치가 보호되어 있거나 밀어넣을 권한이 없습니다.";
        }

        /// <summary>
        /// 원격 연산의 공통 선행 조건을 검사하고, 실패했을 때 덧붙일 안내를 미리 계산한다.
        /// Pull·Push·원격 확인이 글자 그대로 같은 검사를 하므로 한 곳에 둔다 — 복제해 두면
        /// 한쪽 문구만 고쳐지는 일이 실제로 일어난다.
        /// </summary>
        /// <param name="operationName">메시지에 박히는 연산 이름. "Pull", "Push", 또는 "원격 확인".</param>
        /// <returns>안내할 것이 없으면 <c>null</c>. 호출자는 <c>null</c>이면 원본 예외를 그대로 둔다.</returns>
        private static string? ValidateRemoteAndBuildGuidance(Repository repo, string repoPath, string operationName)
        {
            if (!repo.Network.Remotes.Any())
            {
                throw new GitRemoteNotConfiguredException($"'{repoPath}' 저장소에 원격(remote)이 설정되어 있지 않아 {operationName}할 수 없습니다.");
            }

            // 원격만 있고 추적 브랜치가 없으면 libgit2가 영문 원문으로 거부한다. DBVC 온보딩이 실제로
            // 만들어내는 상태다 - 사용자가 clone하지 않고 직접 git init한 폴더를 매핑하면 여기 걸린다.
            // 추적을 대신 설정해 주지는 않는다. 버튼 하나가 사용자의 git config를 조용히 바꾸면 안 된다.
            if (!repo.Head.IsTracking)
            {
                var branchName = repo.Head.FriendlyName;
                throw new GitRemoteNotConfiguredException(
                    $"'{repoPath}' 저장소의 현재 브랜치 '{branchName}'에 추적 중인 원격 브랜치가 없어 {operationName}할 수 없습니다. " +
                    $"Git 클라이언트에서 'git push -u origin {branchName}'을 한 번 실행해 추적을 설정한 뒤 다시 시도하세요.");
            }

            // Explain은 예외가 아니라 원격 URL과 ssh 실행 파일 유무만 보므로 통신 이전에 한 번 계산한다.
            // IsTracking이 true라도 RemoteName은 비어 있을 수 있다 - 브랜치가 로컬 브랜치를 추적하는 경우
            // (branch.<name>.remote = "." - `git branch --track`, autoSetupMerge = always 등으로 만들어진다)
            // RemoteName은 ""이고 Remotes[""]는 ArgumentNullException을 던진다. remoteUrl을 null로 두면
            // Explain이 Unknown으로 처리해 guidance가 null이 되고, 호출자의 catch가 가로채지 않아
            // 원본 예외가 그대로 전파된다. 그 원본 예외의 정체는 연산마다 다르다 - Pull은
            // Commands.Pull 내부에서 막혀 libgit2의 LibGit2SharpException(영문 원문)이지만,
            // Push는 Network.Push가 이 지점을 아예 통과하지 못하고 System.ArgumentNullException
            // ("Value cannot be null. (Parameter 'name')")을 직접 던진다 - 이 메서드의 어떤
            // catch에도 걸리지 않는다는 점은 둘 다 같다. DBVC 온보딩은 이 설정을 만들지 않는
            // 예외적 상태이므로 동작은 그대로 두고 사실만 정확히 적는다.
            var remoteName = repo.Head.RemoteName;
            var remoteUrl = string.IsNullOrEmpty(remoteName) ? null : repo.Network.Remotes[remoteName]?.Url;

            // SshExecutableLocator만으로는 부족하다 - libgit2의 ssh_exec 전송은 GIT_SSH(_COMMAND) 외에
            // core.sshCommand 설정값도 읽는다. OpenSSH 선택적 기능이 꺼져 있어도 Git for Windows의
            // ssh.exe를 core.sshCommand로 가리키는 구성(사내 PC에서 흔함)은 실제로 SSH가 되므로 여기서 함께 본다.
            var sshAvailable = SshExecutableLocator.IsAvailable()
                || !string.IsNullOrWhiteSpace(repo.Config.Get<string>("core.sshCommand")?.Value);

            return RemoteDiagnostics.Explain(remoteUrl, sshAvailable);
        }

        /// <summary>
        /// Pull에 사용할 <see cref="PullOptions"/>를 만든다. <see cref="ResolveCredentials"/>를
        /// <c>FetchOptions.CredentialsProvider</c>에 실제로 연결하는 지점이 여기다.
        /// <c>internal</c>로 노출해 "연결이 됐는지"와 "요구 플래그가 람다 밖으로 새어 나오는지"를
        /// 직접 단위 테스트로 검증할 수 있게 한다 - <see cref="ResolveCredentials"/> 자체를
        /// 테스트하는 것만으로는 이 배선이 실제로 붙어 있는지 증명하지 못한다.
        /// </summary>
        internal static PullOptions BuildPullOptions(Action onUserCredentialsRequired)
        {
            return new PullOptions
            {
                FetchOptions = new FetchOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) =>
                    {
                        var credentials = ResolveCredentials(types, out var needsUserCredentials);
                        if (needsUserCredentials) onUserCredentialsRequired();
                        return credentials;
                    }
                }
            };
        }

        /// <summary>
        /// Push에 사용할 <see cref="PushOptions"/>를 만든다.
        /// <c>internal</c>로 노출하는 이유는 <see cref="BuildPullOptions"/>와 같다 —
        /// 파일 경로 원격을 쓰는 단위 테스트는 자격 증명 콜백도, 서버의 상태 보고도 거치지 않으므로
        /// 이 배선이 실제로 붙어 있는지는 여기서만 검증할 수 있다.
        /// </summary>
        /// <param name="onPushStatusError">
        /// 서버가 ref 갱신을 거부했을 때 호출된다. 이것을 연결하지 않으면
        /// <c>Network.Push</c>가 정상 반환해 실패가 성공으로 보고된다.
        /// </param>
        internal static PushOptions BuildPushOptions(
            Action onUserCredentialsRequired,
            Action<PushStatusError> onPushStatusError)
        {
            return new PushOptions
            {
                CredentialsProvider = (url, usernameFromUrl, types) =>
                {
                    var credentials = ResolveCredentials(types, out var needsUserCredentials);
                    if (needsUserCredentials) onUserCredentialsRequired();
                    return credentials;
                },
                OnPushStatusError = error => onPushStatusError(error)
            };
        }

        /// <summary>
        /// 원격이 요구하는 자격 증명 종류를 보고 무엇을 넘길지 정한다.
        /// libgit2는 SSH를 시스템 ssh 실행 파일에 위임하므로 SSH 원격은 이 콜백을 거치지 않는다.
        /// 뒤집으면 이 콜백이 호출됐다는 것은 원격이 HTTPS이고 자격 증명을 요구한다는 뜻이다.
        /// DefaultCredentials를 계속 반환하는 이유는 비용이 없고, 원격에 Kerberos가 붙어 있으면
        /// 그대로 통하기 때문이다.
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
        /// 커밋 이력을 최신순으로 반환한다. (설계 3.2 History)
        /// <paramref name="relativeFilePath"/>가 비면 저장소 전체 이력을 반환한다 —
        /// 커밋 직후에는 변경 목록이 비어 화면에서 선택할 객체 자체가 없기 때문이다.
        /// </summary>
        public IReadOnlyList<CommitInfo> GetHistory(string serverName, string databaseName, string? relativeFilePath)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return new List<CommitInfo>();

            try
            {
                using var repo = new Repository(repoPath);

                // repo.Commits의 기본 정렬은 시간 역순이고, QueryBy도 같은 순서를 따른다.
                var commits = string.IsNullOrWhiteSpace(relativeFilePath)
                    ? repo.Commits.AsEnumerable()
                    : repo.Commits.QueryBy(NormalizePath(relativeFilePath!)).Select(entry => entry.Commit);

                return commits
                    .Select(commit => new CommitInfo
                    {
                        Sha = commit.Sha,
                        ParentSha = commit.Parents.FirstOrDefault()?.Sha,
                        Message = commit.Message,
                        Author = commit.Author.Name,
                        Date = commit.Author.When
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                var scope = string.IsNullOrWhiteSpace(relativeFilePath) ? "(저장소 전체)" : relativeFilePath;
                Debug.WriteLine($"GitManager.GetHistory failed for '{scope}': {ex.Message}");
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

        /// <summary>
        /// 특정 커밋 시점의 파일 내용을 반환한다. 커밋이나 파일이 없으면 null을 반환한다.
        /// </summary>
        public string? GetFileContentAtCommit(string serverName, string databaseName, string relativeFilePath, string commitSha)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || string.IsNullOrWhiteSpace(relativeFilePath) || string.IsNullOrWhiteSpace(commitSha)) return null;

            try
            {
                using var repo = new Repository(repoPath);
                var commit = repo.Lookup<Commit>(commitSha);
                if (commit == null) return null;

                return ReadBlobText(commit, NormalizePath(relativeFilePath));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetFileContentAtCommit failed for '{relativeFilePath}' at '{commitSha}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 특정 커밋의 부모 커밋 시점 파일 내용을 반환한다.
        /// 최초 커밋(부모가 없는 경우)이면 빈 문자열("")을 반환한다.
        /// 커밋이 없거나 조회 실패 시 null을 반환한다.
        /// </summary>
        public string? GetFileContentAtCommitParent(string serverName, string databaseName, string relativeFilePath, string commitSha)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || string.IsNullOrWhiteSpace(relativeFilePath) || string.IsNullOrWhiteSpace(commitSha)) return null;

            try
            {
                using var repo = new Repository(repoPath);
                var commit = repo.Lookup<Commit>(commitSha);
                if (commit == null) return null;

                var parent = commit.Parents.FirstOrDefault();
                if (parent == null) return string.Empty;

                return ReadBlobText(parent, NormalizePath(relativeFilePath));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetFileContentAtCommitParent failed for '{relativeFilePath}' at '{commitSha}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 특정 커밋에서 변경된 파일 목록과 각 파일의 상태(Added, Modified, Deleted)를 반환한다.
        /// 부모 커밋과의 Tree Diff를 비교하여 변경 사항을 조회하며, 최초 커밋인 경우 부모 Tree는 null로 비교된다.
        /// </summary>
        public IReadOnlyList<HistoryChangedFile> GetChangedFilesAtCommit(string serverName, string databaseName, string commitSha)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null || string.IsNullOrWhiteSpace(commitSha)) return new List<HistoryChangedFile>();

            try
            {
                using var repo = new Repository(repoPath);
                var commit = repo.Lookup<Commit>(commitSha);
                if (commit == null) return new List<HistoryChangedFile>();

                var parentTree = commit.Parents.FirstOrDefault()?.Tree;
                var changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);

                return changes.Select(c => new HistoryChangedFile
                {
                    State = c.Status == ChangeKind.Added ? HistoryChangedFileState.Added :
                            c.Status == ChangeKind.Deleted ? HistoryChangedFileState.Deleted :
                            HistoryChangedFileState.Modified,
                    RelativePath = c.Path
                }).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.GetChangedFilesAtCommit failed for '{commitSha}': {ex.Message}");
                return new List<HistoryChangedFile>();
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

        /// <summary>
        /// 저장소 없이 ssh 사용 가능 여부를 본다.
        ///
        /// Pull·Push는 repo.Config에서 core.sshCommand도 함께 보지만 clone 시점에는 저장소가
        /// 아직 없다. Configuration.BuildFrom(null)이 전역·시스템 config를 열어 주므로
        /// 같은 판정이 나온다 — OpenSSH 선택적 기능이 꺼져 있어도 Git for Windows의 ssh.exe를
        /// core.sshCommand로 가리키는 구성(사내 PC에서 흔함)은 실제로 SSH가 된다.
        /// 이것을 빠뜨리면 그런 PC에서 "OpenSSH 클라이언트를 설치하세요"라는 틀린 안내가 나간다.
        /// </summary>
        private static bool IsSshAvailableWithoutRepository()
        {
            if (SshExecutableLocator.IsAvailable()) return true;

            try
            {
                using var config = Configuration.BuildFrom(null);
                return !string.IsNullOrWhiteSpace(config.Get<string>("core.sshCommand")?.Value);
            }
            catch (Exception ex)
            {
                // config를 못 읽는 것이 clone 실패의 원인은 아니다. 안내만 덜 정확해진다.
                Debug.WriteLine($"GitManager.IsSshAvailableWithoutRepository failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 폴더를 통째로 지운다. .git 내부에는 읽기 전용 파일(pack 등)이 있어
        /// 속성을 먼저 풀지 않으면 Directory.Delete가 거부된다.
        ///
        /// 지우지 못해도 예외를 밖으로 내지 않는다 — 호출자는 이미 실패를 보고하는 중이고,
        /// 정리 실패로 원래 원인이 가려지면 사용자가 무엇을 고쳐야 하는지 알 수 없다.
        /// </summary>
        private static void DeleteDirectoryTree(string path)
        {
            if (!Directory.Exists(path)) return;

            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitManager.DeleteDirectoryTree failed for '{path}': {ex.Message}");
            }
        }
    }
}
