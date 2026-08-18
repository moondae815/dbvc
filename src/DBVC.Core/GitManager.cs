using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        /// 원격이 사용자 자격 증명을 요구하면 <see cref="GitAuthenticationException"/>을,
        /// 그 외에 원격과 통신하지 못했고 안내할 원인이 있으면 <see cref="GitRemoteException"/>을 던진다.
        /// 원격이 없거나 현재 브랜치에 추적 중인 원격 브랜치가 없으면 <see cref="InvalidOperationException"/>을 던진다.
        /// </summary>
        public bool PullChanges(string serverName, string databaseName)
        {
            var repoPath = ResolveRepoPath(serverName, databaseName);
            if (repoPath == null) return false;

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

            return true;
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
        /// Pull과 Push가 글자 그대로 같은 검사를 하므로 한 곳에 둔다 — 복제해 두면
        /// 한쪽 문구만 고쳐지는 일이 실제로 일어난다.
        /// </summary>
        /// <param name="operationName">메시지에 박히는 연산 이름. "Pull" 또는 "Push".</param>
        /// <returns>안내할 것이 없으면 <c>null</c>. 호출자는 <c>null</c>이면 원본 예외를 그대로 둔다.</returns>
        private static string? ValidateRemoteAndBuildGuidance(Repository repo, string repoPath, string operationName)
        {
            if (!repo.Network.Remotes.Any())
            {
                throw new InvalidOperationException($"'{repoPath}' 저장소에 원격(remote)이 설정되어 있지 않아 {operationName}할 수 없습니다.");
            }

            // 원격만 있고 추적 브랜치가 없으면 libgit2가 영문 원문으로 거부한다. DBVC 온보딩이 실제로
            // 만들어내는 상태다 - 사용자가 clone하지 않고 직접 git init한 폴더를 매핑하면 여기 걸린다.
            // 추적을 대신 설정해 주지는 않는다. 버튼 하나가 사용자의 git config를 조용히 바꾸면 안 된다.
            if (!repo.Head.IsTracking)
            {
                var branchName = repo.Head.FriendlyName;
                throw new InvalidOperationException(
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
