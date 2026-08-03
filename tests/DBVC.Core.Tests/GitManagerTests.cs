using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LibGit2Sharp;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class GitManagerTests
    {
        private readonly System.Collections.Generic.List<string> _tempDirs = new System.Collections.Generic.List<string>();

        [TearDown]
        public void CleanUpTempDirs()
        {
            foreach (var dir in _tempDirs)
            {
                TryDeleteDirectory(dir);
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_git_" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                // .git 내부에는 읽기 전용 파일이 있을 수 있다.
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }

        private static readonly Signature TestSignature = new Signature("Test", "test@example.com", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        /// <summary>커밋 1개를 가진 저장소를 만든다.</summary>
        private string NewRepoWithCommit(string fileName = "dbo/Tables/Users.sql", string content = "CREATE TABLE Users (Id INT);")
        {
            var path = NewTempDir();
            Repository.Init(path);
            WriteRepoFile(path, fileName, content);
            using var repo = new Repository(path);
            Commands.Stage(repo, "*");
            repo.Commit("initial", TestSignature, TestSignature);
            return path;
        }

        private static void WriteRepoFile(string repoPath, string relativePath, string content)
        {
            var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        private GitManager NewGitManager(string serverName, string databaseName, string repoPath)
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var config = new ConfigManager(configPath);
            config.AddMapping(serverName, databaseName, repoPath);
            return new GitManager(config);
        }

        // ---------- GetStatus ----------

        [Test]
        public void GetStatus_ReturnsUnknown_WhenPathIsNotARepository()
        {
            var manager = new GitManager();
            Assert.That(manager.GetStatus(Path.Combine(Path.GetTempPath(), "definitely_not_a_repo_" + Guid.NewGuid().ToString("N"))),
                Is.EqualTo("Unknown"));
        }

        [Test]
        public void GetStatus_ReturnsClean_ForRepositoryWithNoChanges()
        {
            var repoPath = NewRepoWithCommit();
            var manager = new GitManager();

            Assert.That(manager.GetStatus(repoPath), Is.EqualTo("Clean"));
        }

        [Test]
        public void GetStatus_ReturnsModified_WhenWorkingTreeHasChanges()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");
            var manager = new GitManager();

            Assert.That(manager.GetStatus(repoPath), Is.EqualTo("Modified"));
        }

        [Test]
        public void GetStatusForDatabase_ReturnsUnknown_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var manager = new GitManager(new ConfigManager(configPath));

            Assert.That(manager.GetStatusForDatabase("LocalServer", "SalesDB"), Is.EqualTo("Unknown"));
        }

        [Test]
        public void GetStatusForDatabase_UsesConfigManagerMapping()
        {
            var repoPath = NewRepoWithCommit();
            var manager = NewGitManager("LocalServer", "SalesDB", repoPath);

            Assert.That(manager.GetStatusForDatabase("LocalServer", "SalesDB"), Is.EqualTo("Clean"));
        }

        // ---------- IsRepository ----------

        [Test]
        public void IsRepository_ReturnsTrue_ForAnInitializedRepository()
        {
            Assert.That(new GitManager().IsRepository(NewRepoWithCommit()), Is.True);
        }

        [Test]
        public void IsRepository_ReturnsFalse_ForAPlainDirectory()
        {
            var path = NewTempDir();
            Directory.CreateDirectory(path);

            Assert.That(new GitManager().IsRepository(path), Is.False,
                "git init되지 않은 폴더를 매핑하면 이후 모든 동작이 조용히 실패합니다");
        }

        [Test]
        public void IsRepository_ReturnsFalse_ForAMissingPath()
        {
            Assert.That(
                new GitManager().IsRepository(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N"))),
                Is.False);
        }

        // ---------- GetChangedFiles ----------

        [Test]
        public void GetChangedFiles_ReturnsEmpty_ForCleanRepository()
        {
            var repoPath = NewRepoWithCommit();
            var manager = new GitManager();

            Assert.That(manager.GetChangedFiles(repoPath), Is.Empty);
        }

        [Test]
        public void GetChangedFiles_ListsModifiedAndUntrackedFilesWithForwardSlashes()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");
            WriteRepoFile(repoPath, "dbo/Views/vw_Users.sql", "CREATE VIEW vw_Users AS SELECT 1 AS X;");
            var manager = new GitManager();

            var changed = manager.GetChangedFiles(repoPath);

            Assert.That(changed, Does.Contain("dbo/Tables/Users.sql"));
            Assert.That(changed, Does.Contain("dbo/Views/vw_Users.sql"));
        }

        [Test]
        public void GetChangedFileStates_ClassifiesAddedModifiedAndDeleted()
        {
            // 기준선: Users.sql과 Temp.sql이 커밋된 상태
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Temp.sql", "CREATE TABLE Temp (Id INT);");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                repo.Commit("add temp", TestSignature, TestSignature);
            }

            // 추적 중인 파일 수정 / 신규 파일 추가 / 추적 중인 파일 삭제
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");
            WriteRepoFile(repoPath, "dbo/Views/vw_Users.sql", "CREATE VIEW vw_Users AS SELECT 1 AS X;");
            File.Delete(Path.Combine(repoPath, "dbo", "Tables", "Temp.sql"));

            var states = new GitManager().GetChangedFileStates(repoPath);

            Assert.That(states["dbo/Tables/Users.sql"], Is.EqualTo("Modified"));
            Assert.That(states["dbo/Views/vw_Users.sql"], Is.EqualTo("Added"));
            Assert.That(states["dbo/Tables/Temp.sql"], Is.EqualTo("Deleted"));
        }

        [Test]
        public void GetChangedFileStates_ReportsModified_ForTrackedFileEditedAfterCommit()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");

            var states = new GitManager().GetChangedFileStates(repoPath);

            Assert.That(states["dbo/Tables/Users.sql"], Is.EqualTo("Modified"));
        }

        [Test]
        public void GetChangedFiles_ReturnsEmpty_WhenPathIsNotARepository()
        {
            var manager = new GitManager();
            Assert.That(manager.GetChangedFiles(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N"))), Is.Empty);
        }

        // ---------- CommitChanges ----------

        [Test]
        public void CommitChanges_ThrowsException_IfRepoNotFound()
        {
            var path = NewTempDir();
            Directory.CreateDirectory(path);
            var git = NewGitManager("localhost", "testdb", path);

            Assert.Throws<RepositoryNotFoundException>(() => git.CommitChanges("localhost", "testdb", "test"));
        }

        [Test]
        public void CommitChanges_ReturnsFalse_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var git = new GitManager(new ConfigManager(configPath));

            Assert.That(git.CommitChanges("localhost", "testdb", "test"), Is.False);
        }

        [Test]
        public void CommitChanges_StagesAndCommitsFiles_WhenRepoExists()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "Add Orders");

            Assert.That(result, Is.True);
            using var repo = new Repository(repoPath);
            Assert.That(repo.Head.Tip.Message.TrimEnd(), Is.EqualTo("Add Orders"));
        }

        [Test]
        public void CommitChanges_ReturnsFalse_WhenThereIsNothingToCommit()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "empty");

            Assert.That(result, Is.False, "스테이징할 변경이 없으면 예외 대신 false를 반환해야 합니다");
        }

        [Test]
        public void CommitChanges_StagesOnlyTheSpecifiedPaths()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            WriteRepoFile(repoPath, "dbo/Tables/Products.sql", "CREATE TABLE Products (Id INT);");
            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "Only orders", new[] { "dbo/Tables/Orders.sql" });

            Assert.That(result, Is.True);
            using var repo = new Repository(repoPath);
            var committedPaths = repo.Diff
                .Compare<TreeChanges>(repo.Head.Tip.Parents.First().Tree, repo.Head.Tip.Tree)
                .Select(c => c.Path)
                .ToList();

            Assert.That(committedPaths, Is.EqualTo(new[] { "dbo/Tables/Orders.sql" }));
            Assert.That(repo.RetrieveStatus().Untracked.Select(e => e.FilePath), Does.Contain("dbo/Tables/Products.sql"),
                "선택되지 않은 파일은 커밋되지 않고 남아 있어야 합니다");
        }

        [Test]
        public void CommitChanges_CommitsTheDeletion_WhenTheFileIsGoneFromTheWorkingTree()
        {
            // 드롭된 객체 파일 정리 기능(WorkingTreeCleaner) 전체가 이 동작에 기대고 있다:
            // Commands.Stage(repo, explicitPaths)가 작업 트리에 없는 경로에 대해 삭제를 스테이징해야 한다.
            var repoPath = NewRepoWithCommit();
            File.Delete(Path.Combine(repoPath, "dbo", "Tables", "Users.sql"));
            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "Drop Users", new[] { "dbo/Tables/Users.sql" });

            Assert.That(result, Is.True);
            using var repo = new Repository(repoPath);
            Assert.That(repo.Head.Tip.Tree["dbo/Tables/Users.sql"], Is.Null,
                "삭제된 파일이 새 HEAD 트리에는 남아 있으면 안 됩니다");
        }

        [Test]
        public void CommitChanges_CommitsTheDeletionAlongsideAModification_InTheSameCall()
        {
            // 조용한 소실 시나리오를 Git 계층에서 검증한다: 삭제 하나와 수정 하나를
            // 명시적 경로로 한 번에 커밋해도 삭제는 반영되고 수정 내용도 그대로 담겨야 한다.
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            using (var setupRepo = new Repository(repoPath))
            {
                Commands.Stage(setupRepo, "*");
                setupRepo.Commit("add orders", TestSignature, TestSignature);
            }

            File.Delete(Path.Combine(repoPath, "dbo", "Tables", "Users.sql"));
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT, Name NVARCHAR(50));");

            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "Drop Users, modify Orders",
                new[] { "dbo/Tables/Users.sql", "dbo/Tables/Orders.sql" });

            Assert.That(result, Is.True);
            using var repo = new Repository(repoPath);
            Assert.That(repo.Head.Tip.Tree["dbo/Tables/Users.sql"], Is.Null,
                "삭제는 함께 커밋된 수정과 무관하게 반영되어야 합니다");

            var ordersEntry = repo.Head.Tip.Tree["dbo/Tables/Orders.sql"];
            Assert.That(ordersEntry, Is.Not.Null);
            var ordersBlob = (Blob)ordersEntry.Target;
            Assert.That(ordersBlob.GetContentText(), Is.EqualTo("CREATE TABLE Orders (Id INT, Name NVARCHAR(50));"),
                "같은 커밋에 포함된 수정 내용도 그대로 반영되어야 합니다");
        }

        // ---------- GetHistory ----------

        [Test]
        public void GetHistory_ReturnsCommitsTouchingTheFile_NewestFirst()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                repo.Commit("second", TestSignature, TestSignature);
            }
            var git = NewGitManager("localhost", "testdb", repoPath);

            var history = git.GetHistory("localhost", "testdb", "dbo/Tables/Users.sql");

            Assert.That(history.Count, Is.EqualTo(2));
            Assert.That(history[0].Message.TrimEnd(), Is.EqualTo("second"));
            Assert.That(history[1].Message.TrimEnd(), Is.EqualTo("initial"));
            Assert.That(history[0].Sha, Is.Not.Empty);
        }

        [Test]
        public void GetHistory_ReturnsEmpty_ForUnknownFile()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", repoPath);

            Assert.That(git.GetHistory("localhost", "testdb", "dbo/Tables/Nope.sql"), Is.Empty);
        }

        // ---------- GetFileContentAtHead ----------

        [Test]
        public void GetFileContentAtHead_ReturnsCommittedContent()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", repoPath);

            var content = git.GetFileContentAtHead("localhost", "testdb", "dbo/Tables/Users.sql");

            Assert.That(content, Is.EqualTo("CREATE TABLE Users (Id INT);"));
        }

        [Test]
        public void GetFileContentAtHead_ReturnsNull_ForFileNotInRepository()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", repoPath);

            Assert.That(git.GetFileContentAtHead("localhost", "testdb", "dbo/Tables/New.sql"), Is.Null,
                "신규 객체는 Git에 없으므로 null이어야 하고, Diff는 좌측을 비워 표시할 수 있어야 합니다");
        }

        // ---------- GetFileContentBeforeLastCommit (Rollback) ----------

        [Test]
        public void GetFileContentBeforeLastCommit_ReturnsTheStateJustBeforeTheMostRecentCommit()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                repo.Commit("second", TestSignature, TestSignature);
            }
            var git = NewGitManager("localhost", "testdb", repoPath);

            var content = git.GetFileContentBeforeLastCommit("localhost", "testdb", "dbo/Tables/Users.sql");

            Assert.That(content, Is.EqualTo("CREATE TABLE Users (Id INT);"),
                "Rollback은 마지막 커밋 직전 상태를 되살려야 합니다");
        }

        [Test]
        public void GetFileContentBeforeLastCommit_ReturnsNull_WhenTheFileWasOnlyEverCommittedOnce()
        {
            // 최초 생성 이후 수정이 없으면 되돌릴 이전 상태가 없다.
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", repoPath);

            Assert.That(git.GetFileContentBeforeLastCommit("localhost", "testdb", "dbo/Tables/Users.sql"), Is.Null);
        }

        [Test]
        public void GetFileContentBeforeLastCommit_ReturnsNull_ForFileWithNoHistory()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", repoPath);

            Assert.That(git.GetFileContentBeforeLastCommit("localhost", "testdb", "dbo/Tables/Nope.sql"), Is.Null);
        }

        [Test]
        public void GetFileContentBeforeLastCommit_ReturnsPriorContent_EvenWhenTheFileWasLaterDeleted()
        {
            var repoPath = NewRepoWithCommit();
            File.Delete(Path.Combine(repoPath, "dbo", "Tables", "Users.sql"));
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                repo.Commit("drop users", TestSignature, TestSignature);
            }
            var git = NewGitManager("localhost", "testdb", repoPath);

            Assert.That(git.GetFileContentBeforeLastCommit("localhost", "testdb", "dbo/Tables/Users.sql"),
                Is.EqualTo("CREATE TABLE Users (Id INT);"),
                "삭제된 객체야말로 Rollback 대상이므로 이전 내용을 복원할 수 있어야 합니다");
        }

        // ---------- PullChanges ----------

        [Test]
        public void PullChanges_FastForwards_WhenRemoteHasNewCommits()
        {
            var originPath = NewRepoWithCommit();
            var clonePath = NewTempDir();
            Repository.Clone(originPath, clonePath);

            WriteRepoFile(originPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            using (var origin = new Repository(originPath))
            {
                Commands.Stage(origin, "*");
                origin.Commit("remote change", TestSignature, TestSignature);
            }

            var git = NewGitManager("localhost", "testdb", clonePath);

            var result = git.PullChanges("localhost", "testdb");

            Assert.That(result, Is.True);
            Assert.That(File.Exists(Path.Combine(clonePath, "dbo", "Tables", "Orders.sql")), Is.True,
                "Pull 후 원격 커밋의 파일이 로컬에 존재해야 합니다");
        }

        [Test]
        public void PullChanges_ReturnsFalse_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var git = new GitManager(new ConfigManager(configPath));

            Assert.That(git.PullChanges("localhost", "testdb"), Is.False);
        }

        [Test]
        public void PullChanges_ThrowsMergeConflictException_AndRestoresHead_OnConflict()
        {
            var originPath = NewRepoWithCommit();
            var clonePath = NewTempDir();
            Repository.Clone(originPath, clonePath);

            // 같은 파일을 원격과 로컬에서 서로 다르게 수정 -> 충돌
            WriteRepoFile(originPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, RemoteCol INT);");
            using (var origin = new Repository(originPath))
            {
                Commands.Stage(origin, "*");
                origin.Commit("remote edit", TestSignature, TestSignature);
            }

            WriteRepoFile(clonePath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, LocalCol INT);");
            string localHeadBefore;
            using (var clone = new Repository(clonePath))
            {
                Commands.Stage(clone, "*");
                clone.Commit("local edit", TestSignature, TestSignature);
                localHeadBefore = clone.Head.Tip.Sha;
            }

            var git = NewGitManager("localhost", "testdb", clonePath);

            Assert.Throws<MergeConflictException>(() => git.PullChanges("localhost", "testdb"));

            using (var clone = new Repository(clonePath))
            {
                Assert.That(clone.Head.Tip.Sha, Is.EqualTo(localHeadBefore),
                    "충돌 시 Pull을 중단하고 HEAD를 원래대로 되돌려야 합니다");
                Assert.That(clone.Index.Conflicts, Is.Empty,
                    "충돌 인덱스가 남아 저장소가 병합 중 상태로 방치되면 안 됩니다");
                Assert.That(clone.Info.CurrentOperation, Is.EqualTo(CurrentOperation.None),
                    "MERGE_HEAD가 남으면 사용자의 다음 커밋이 조용히 병합 커밋이 됩니다. " +
                    "Index.Conflicts만으로는 이 상태를 잡지 못합니다");
            }
        }

        // ---------- BuildPullOptions (자격 증명 배선) ----------

        [Test]
        public void BuildPullOptions_WiresResolveCredentialsIntoFetchOptions()
        {
            // ResolveCredentials가 단위 테스트를 통과하는 것과, 그것이 실제로 PullChanges가 쓰는
            // PullOptions에 연결되어 있는 것은 별개다. FetchOptions나 CredentialsProvider가 비어 있으면
            // 인증이 필요한 원격에서 항상 실패하는데, 로컬 경로 원격을 쓰는 다른 Pull 테스트들은
            // 이 배선이 없어도 전부 통과하므로 이 사실을 잡아내지 못한다.
            var options = GitManager.BuildPullOptions(() => { });

            Assert.That(options.FetchOptions, Is.Not.Null);
            Assert.That(options.FetchOptions.CredentialsProvider, Is.Not.Null,
                "CredentialsProvider가 연결되어 있지 않으면 자격 증명을 요구하는 원격에서 항상 실패합니다");
        }

        [Test]
        public void BuildPullOptions_InvokesTheCallback_OnlyWhenTheRemoteRequiresUserCredentials()
        {
            var requiresUserCredentialsCallCount = 0;
            var options = GitManager.BuildPullOptions(() => requiresUserCredentialsCallCount++);
            var provider = options.FetchOptions.CredentialsProvider!;

            // Default를 지원하는 원격: 통합 인증으로 처리되므로 콜백이 불리면 안 된다.
            provider("https://example.com/repo.git", null,
                SupportedCredentialTypes.UsernamePassword | SupportedCredentialTypes.Default);
            Assert.That(requiresUserCredentialsCallCount, Is.EqualTo(0),
                "Default 플래그가 있는데도 콜백이 불리면 GitAuthenticationException이 자격 증명이 통하는 " +
                "원격에도 잘못 던져집니다");

            // Default를 지원하지 않는 원격: 콜백이 불려야 PullChanges가 GitAuthenticationException으로 감쌀 수 있다.
            provider("https://example.com/repo.git", null, SupportedCredentialTypes.UsernamePassword);
            Assert.That(requiresUserCredentialsCallCount, Is.EqualTo(1),
                "Default 플래그가 없는데도 콜백이 안 불리면 requiresUserCredentials 플래그가 람다 밖으로 " +
                "새어 나오지 못해 GitAuthenticationException이 던져지지 않습니다");
        }

        [Test]
        public void ResolveCredentials_UsesWindowsIntegratedAuth_WhenTheRemoteSupportsIt()
        {
            var credentials = GitManager.ResolveCredentials(
                SupportedCredentialTypes.UsernamePassword | SupportedCredentialTypes.Default,
                out var requiresUserCredentials);

            Assert.That(credentials, Is.InstanceOf<DefaultCredentials>());
            Assert.That(requiresUserCredentials, Is.False,
                "Default를 지원하는 원격은 통합 인증으로 처리되므로 자격 증명 요구로 표시하면 안 됩니다");
        }

        [Test]
        public void ResolveCredentials_FlagsTheRemote_WhenOnlyUsernamePasswordIsSupported()
        {
            var credentials = GitManager.ResolveCredentials(
                SupportedCredentialTypes.UsernamePassword,
                out var requiresUserCredentials);

            Assert.That(credentials, Is.InstanceOf<DefaultCredentials>(),
                "핸들러는 Credentials를 반드시 돌려줘야 합니다. 여기서 하는 일은 실패를 막는 것이 아니라 원인을 기록하는 것입니다");
            Assert.That(requiresUserCredentials, Is.True,
                "Default를 지원하지 않으면 GitAuthenticationException으로 감쌀 근거가 됩니다");
        }

        [Test]
        public void PullChanges_ThrowsWorkingTreeConflictException_WhenUncommittedChangesOverlapTheIncomingOnes()
        {
            var originPath = NewRepoWithCommit();
            var clonePath = NewTempDir();
            Repository.Clone(originPath, clonePath);

            // 원격이 파일을 바꿔 커밋한다.
            WriteRepoFile(originPath, "dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, RemoteCol INT);");
            using (var origin = new Repository(originPath))
            {
                Commands.Stage(origin, "*");
                origin.Commit("remote edit", TestSignature, TestSignature);
            }

            // 로컬은 같은 파일을 커밋하지 않은 채 수정한다. 충돌 커밋이 아니라 미커밋 변경이다.
            const string localContent = "CREATE TABLE Users (Id INT, LocalUncommitted INT);";
            WriteRepoFile(clonePath, "dbo/Tables/Users.sql", localContent);

            string headBefore;
            using (var clone = new Repository(clonePath))
            {
                headBefore = clone.Head.Tip.Sha;
            }

            var git = NewGitManager("localhost", "testdb", clonePath);

            var ex = Assert.Throws<WorkingTreeConflictException>(() => git.PullChanges("localhost", "testdb"));

            Assert.That(ex!.InnerException, Is.InstanceOf<CheckoutConflictException>(),
                "원인을 보존해야 진단할 수 있습니다");
            Assert.That(ex.Message, Does.Contain("저장소는 변경되지 않았습니다"),
                "이 경로의 핵심 정보는 '잃은 것이 없다'는 사실입니다");

            using (var clone = new Repository(clonePath))
            {
                Assert.That(clone.Head.Tip.Sha, Is.EqualTo(headBefore),
                    "병합이 시작되지 않았으므로 HEAD가 움직이면 안 됩니다");
                Assert.That(clone.Index.Conflicts, Is.Empty,
                    "AbortMerge를 부르지 않아도 저장소가 병합 중 상태로 남지 않아야 합니다");
            }

            Assert.That(File.ReadAllText(Path.Combine(clonePath, "dbo", "Tables", "Users.sql")),
                Is.EqualTo(localContent),
                "미커밋 변경이 그대로 남아 있어야 합니다. 이것이 MergeConflictException과 갈리는 지점입니다");
        }

        [Test]
        public void PullChanges_ThrowsGitAuthenticationException_WhenTheRemoteChallengesWithBasicAuth()
        {
            // 단위 테스트로 격리된 BuildPullOptions/ResolveCredentials가 옳아도, PullChanges가
            // 그 옵션을 실제로 Commands.Pull에 넘기지 않으면 이 경로는 절대 실행되지 않는다.
            // 로컬 파일 경로 원격을 쓰는 다른 Pull 테스트는 자격 증명 콜백 자체가 불리지 않으므로
            // 그 결함을 잡지 못한다. 여기서는 Basic 인증을 요구하는 실제 HTTP 서버를 띄워
            // PullChanges 전체 경로(빌드된 PullOptions -> Commands.Pull -> 자격 증명 콜백 호출 ->
            // requiresUserCredentials 전파 -> GitAuthenticationException 변환)를 end-to-end로 검증한다.
            using var server = new BasicAuthChallengeServer();
            var clonePath = NewRepoWithCommit();
            using (var repo = new Repository(clonePath))
            {
                repo.Network.Remotes.Add("origin", server.Url);
                // Commands.Pull은 현재 브랜치에 추적 정보(remote/merge)가 있어야 동작한다.
                // 실제 원격에서 fetch할 수 없으므로(서버가 401만 준다) 수동으로 설정한다.
                var branchName = repo.Head.FriendlyName;
                repo.Config.Set($"branch.{branchName}.remote", "origin");
                repo.Config.Set($"branch.{branchName}.merge", $"refs/heads/{branchName}");
            }
            var git = NewGitManager("localhost", "testdb", clonePath);

            var ex = Assert.Throws<GitAuthenticationException>(() => git.PullChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("자격 증명"),
                "GitManager.ResolveCredentials가 실제로 Commands.Pull의 CredentialsProvider로 호출됐어야 이 경로에 도달합니다");
        }

        private sealed class BasicAuthChallengeServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly Thread _thread;
            public string Url { get; }

            public BasicAuthChallengeServer()
            {
                var port = GetFreePort();
                Url = $"http://127.0.0.1:{port}/repo.git";
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _thread = new Thread(ServeLoop) { IsBackground = true };
                _thread.Start();
            }

            private void ServeLoop()
            {
                while (true)
                {
                    HttpListenerContext context;
                    try { context = _listener.GetContext(); }
                    catch { return; }
                    try
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Headers.Add("WWW-Authenticate", "Basic realm=\"dbvc-test\"");
                        context.Response.Close();
                    }
                    catch { }
                }
            }

            private static int GetFreePort()
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
                _thread.Join(TimeSpan.FromSeconds(2));
            }
        }

        // ---------- Constructor ----------

        [Test]
        public void Constructor_ThrowsArgumentNullException_WhenConfigManagerIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new GitManager(null!));
        }
    }
}
