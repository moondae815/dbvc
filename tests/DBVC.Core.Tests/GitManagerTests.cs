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
// LibGit2Sharp도 최상위 PushResult 클래스를 갖고 있어 두 using만으로는 모호하다(CS0104).
// 이 파일이 검증하는 것은 DBVC의 PushResult이므로 별칭으로 고정한다.
using PushResult = DBVC.Core.Models.PushResult;

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

        /// <summary>
        /// bare 원격과 그것을 clone한 로컬 저장소를 만든다.
        /// 원격이 bare가 아니면 "체크아웃된 브랜치는 갱신할 수 없다"로 push가 거부되어,
        /// 우리가 검증하려는 거부 경로와 구분되지 않는다.
        /// </summary>
        private (string LocalPath, string OriginPath) NewClonedRepoWithBareOrigin()
        {
            var seedPath = NewRepoWithCommit();
            var originPath = NewTempDir();
            Repository.Clone(seedPath, originPath, new CloneOptions { IsBare = true });

            var localPath = NewTempDir();
            Repository.Clone(originPath, localPath);
            return (localPath, originPath);
        }

        /// <summary>해당 작업 트리에 파일 하나를 더하고 커밋한다. 커밋 SHA를 준다.</summary>
        private static string CommitOneFile(string repoPath, string relativePath, string content, string message)
        {
            WriteRepoFile(repoPath, relativePath, content);
            using var repo = new Repository(repoPath);
            Commands.Stage(repo, "*");
            return repo.Commit(message, TestSignature, TestSignature).Sha;
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

        // ---------- GetRepositoryState ----------

        [Test]
        public void GetRepositoryState_ReportsCurrentBranch_WhenRepositoryIsClean()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("srv", "db", repoPath);

            var state = git.GetRepositoryState("srv", "db");

            Assert.That(state, Is.Not.Null);
            Assert.That(state!.IsDetached, Is.False);
            Assert.That(state.CurrentBranch, Is.Not.Null.And.Not.Empty);
            Assert.That(state.BlockReason, Is.EqualTo(RepositoryBlockReason.None));
            Assert.That(state.BlockMessage, Is.Null);
        }

        [Test]
        public void GetRepositoryState_BlocksWithMessage_WhenBranchDiffersFromMapping()
        {
            var repoPath = NewRepoWithCommit();
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var config = new ConfigManager(configPath);
            config.AddMapping("srv", "db", repoPath);

            // 실제 브랜치가 무엇이든 존재하지 않을 이름으로 고정해 불일치를 만든다.
            var mapping = config.TryGetMapping("srv", "db")!;
            mapping.Branch = "no-such-branch";
            config.AddMapping(mapping);

            var state = new GitManager(config).GetRepositoryState("srv", "db");

            Assert.That(state!.BlockReason, Is.EqualTo(RepositoryBlockReason.BranchMismatch));
            Assert.That(state.BlockMessage, Does.Contain("no-such-branch"));
        }

        [Test]
        public void GetRepositoryState_ReturnsNull_WhenMappingIsMissing()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var config = new ConfigManager(configPath);

            var state = new GitManager(config).GetRepositoryState("srv", "db");

            Assert.That(state, Is.Null);
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
        public void CommitChanges_ReportsNotMapped_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var git = new GitManager(new ConfigManager(configPath));

            Assert.That(git.CommitChanges("localhost", "testdb", "test"),
                Is.EqualTo(GitCommitResult.NotMapped));
        }

        [Test]
        public void CommitChanges_StagesAndCommitsFiles_WhenRepoExists()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "Add Orders");

            Assert.That(result, Is.EqualTo(GitCommitResult.Committed));
            using var repo = new Repository(repoPath);
            Assert.That(repo.Head.Tip.Message.TrimEnd(), Is.EqualTo("Add Orders"));
        }

        [Test]
        public void CommitChanges_ReportsNothingToCommit_WhenTheFilesAlreadyMatchTheRepository()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "empty");

            // "커밋할 것이 없다"와 "커밋할 수 없다"는 다르다. 전자는 저장소가 이미 DB와 같다는
            // 뜻이라 그 로그 행은 닫아야 하고, 후자는 아무것도 건드리면 안 된다.
            Assert.That(result, Is.EqualTo(GitCommitResult.NothingToCommit),
                "스테이징할 변경이 없으면 예외 대신 NothingToCommit이어야 합니다");
        }

        [Test]
        public void CommitChanges_ReportsNothingSelected_WhenTheCallerPassesNoPaths()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            var git = NewGitManager("localhost", "testdb", repoPath);

            // 더러운 파일이 있어도 아무것도 고르지 않았으면 커밋할 것이 없는 것과 다르다.
            var result = git.CommitChanges("localhost", "testdb", "none", new string[0]);

            Assert.That(result, Is.EqualTo(GitCommitResult.NothingSelected));
        }

        [Test]
        public void CommitChanges_StagesOnlyTheSpecifiedPaths()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            WriteRepoFile(repoPath, "dbo/Tables/Products.sql", "CREATE TABLE Products (Id INT);");
            var git = NewGitManager("localhost", "testdb", repoPath);

            var result = git.CommitChanges("localhost", "testdb", "Only orders", new[] { "dbo/Tables/Orders.sql" });

            Assert.That(result, Is.EqualTo(GitCommitResult.Committed));
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

            Assert.That(result, Is.EqualTo(GitCommitResult.Committed));
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

            Assert.That(result, Is.EqualTo(GitCommitResult.Committed));
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

        /// <summary>
        /// 선택된 객체가 없을 때 화면이 저장소 전체 이력을 보여줄 수 있어야 한다.
        /// 커밋 직후에는 변경 목록이 비어 선택할 객체 자체가 없다.
        /// </summary>
        [Test]
        public void GetHistory_ReturnsEveryCommitInTheRepository_WhenNoFileIsGiven()
        {
            var repoPath = NewRepoWithCommit();
            WriteRepoFile(repoPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            using (var repo = new Repository(repoPath))
            {
                Commands.Stage(repo, "*");
                repo.Commit("second", TestSignature, TestSignature);
            }
            var git = NewGitManager("localhost", "testdb", repoPath);

            var history = git.GetHistory("localhost", "testdb", null);

            Assert.That(history.Count, Is.EqualTo(2));
            Assert.That(history[0].Message.TrimEnd(), Is.EqualTo("second"));
            Assert.That(history[1].Message.TrimEnd(), Is.EqualTo("initial"));
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

            Assert.That(result, Is.EqualTo(PullResult.Pulled));
            Assert.That(File.Exists(Path.Combine(clonePath, "dbo", "Tables", "Orders.sql")), Is.True,
                "Pull 후 원격 커밋의 파일이 로컬에 존재해야 합니다");
        }

        [Test]
        public void PullChanges_ReturnsAlreadyUpToDate_WhenTheRemoteHasNoNewCommits()
        {
            // 원격에 새 커밋이 없으면 libgit2는 MergeStatus.UpToDate를 준다. 이것을
            // FastForward와 구분하지 않으면 화면이 받은 것이 없는데 받았다고 말한다.
            var originPath = NewRepoWithCommit();
            var clonePath = NewTempDir();
            Repository.Clone(originPath, clonePath);

            var git = NewGitManager("localhost", "testdb", clonePath);

            var result = git.PullChanges("localhost", "testdb");

            Assert.That(result, Is.EqualTo(PullResult.AlreadyUpToDate),
                "clone 직후에는 원격에 받아올 새 커밋이 없습니다");
        }

        [Test]
        public void PullChanges_ReturnsPulled_WhenTheMergeCreatesAMergeCommit()
        {
            // Pulled 판정은 MergeStatus.NonFastForward를 곧 병합 커밋이 생겼다는 뜻으로 읽는다.
            // 이 가정은 Commands.Pull의 기본 MergeOptions가 CommitOnSuccess = true라는 데
            // 기대고 있다 - 이 테스트가 그 전제를 직접 확인한다. 원격과 로컬이 서로 다른
            // 파일을 바꿔 히스토리만 갈라지게 하고(충돌 없음), 병합 뒤 HEAD가 부모 둘을
            // 가진 병합 커밋인지까지 본다.
            var originPath = NewRepoWithCommit();
            var clonePath = NewTempDir();
            Repository.Clone(originPath, clonePath);

            WriteRepoFile(originPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);");
            using (var origin = new Repository(originPath))
            {
                Commands.Stage(origin, "*");
                origin.Commit("remote change", TestSignature, TestSignature);
            }

            WriteRepoFile(clonePath, "dbo/Tables/Products.sql", "CREATE TABLE Products (Id INT);");
            using (var clone = new Repository(clonePath))
            {
                Commands.Stage(clone, "*");
                clone.Commit("local change", TestSignature, TestSignature);
            }

            var git = NewGitManager("localhost", "testdb", clonePath);

            var result = git.PullChanges("localhost", "testdb");

            Assert.That(result, Is.EqualTo(PullResult.Pulled));
            using (var clone = new Repository(clonePath))
            {
                Assert.That(clone.Head.Tip.Parents.Count(), Is.EqualTo(2),
                    "NonFastForward가 Pulled로 분류되는 근거는 병합 커밋이 실제로 만들어졌다는 것입니다 - " +
                    "부모가 둘이 아니면 그 전제가 깨진 것입니다");
            }
        }

        [Test]
        public void PullChanges_ReturnsNoMapping_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var git = new GitManager(new ConfigManager(configPath));

            Assert.That(git.PullChanges("localhost", "testdb"), Is.EqualTo(PullResult.NoMapping));
        }

        [Test]
        public void PullChanges_ExplainsInKorean_WhenTheCurrentBranchHasNoUpstream()
        {
            // DBVC 온보딩이 실제로 만들어내는 상태다. 사용자가 clone하지 않고 직접 git init한 폴더를
            // 매핑하면 원격만 있고 추적 브랜치가 없다.
            var originPath = NewRepoWithCommit();
            var localPath = NewRepoWithCommit();

            // 기본 브랜치 이름을 하드코딩하면 안 된다. init.defaultBranch가 설정되지 않은 환경
            // (GitHub Actions 러너 등)에서는 master가 되어 개발 기계에서만 통과하는 테스트가 된다.
            string branchName;
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", originPath);
                branchName = local.Head.FriendlyName;
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PullChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Not.Contain("tracking information"),
                "libgit2의 영문 원문이 사용자에게 그대로 노출되면 안 됩니다 - 가드를 지우면 실패해야 합니다");
            Assert.That(ex.Message, Does.Contain("추적"));
            Assert.That(ex.Message, Does.Contain($"'{branchName}'"),
                "어떤 브랜치를 설정해야 하는지 이름으로 알려줘야 합니다");
            Assert.That(ex.Message, Does.Contain($"git push -u origin {branchName}"),
                "사용자가 그대로 실행할 수 있는 명령을 줘야 합니다");
        }

        [Test]
        public void PullChanges_ExplainsInKorean_WhenTheRepositoryHasNoCommitsYet()
        {
            // unborn HEAD. 같은 가드가 덮지만 브랜치 이름 조회가 터지지 않는지 확인한다.
            var originPath = NewRepoWithCommit();
            var localPath = NewTempDir();
            Repository.Init(localPath);
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", originPath);
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PullChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Not.Contain("tracking information"));
        }

        [Test]
        public void PullChanges_TellsTheUserToSwitchToSsh_WhenTheRemoteIsHttps()
        {
            // 도달 불가능한 HTTPS 원격. 네트워크에 나가지 않고도 자격 증명 요구 이전 단계에서 실패한다.
            //
            // 이 방식이 안전한 이유: 포트 1번 loopback에는 아무것도 붙어 있지 않으므로 connect()가
            // 즉시 RST를 받고 실패한다 - 대기가 없다. 아래의 BasicAuthChallengeServer([Explicit])는
            // 반대로 실제 연결을 "수락"해 HTTP 인증 왕복이 HTTP.sys를 통해 걸리면서 Windows CI를
            // 한 시간 동안 멈추게 한 전례가 있다 - 그래서 그 테스트는 수동 실행 전용으로 남겨 뒀다.
            //
            // 잔여 위험: HTTPS_PROXY가 설정되어 있고 loopback이 no_proxy에 없는 환경에서는 프록시가
            // 407을 응답할 수 있고, 그러면 이 호출이 자격 증명 콜백을 태워 GitAuthenticationException을
            // 던질 수 있다 - GitRemoteException이 아니라. 흔치 않은 환경이라 지금은 감수한다.
            var localPath = NewRepoWithCommit();
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", "https://127.0.0.1:1/nope.git");
                var branchName = local.Head.FriendlyName;
                local.Config.Set($"branch.{branchName}.remote", "origin");
                local.Config.Set($"branch.{branchName}.merge", $"refs/heads/{branchName}");
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<GitRemoteException>(() => git.PullChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("SSH 원격으로 바꾸세요"));
            Assert.That(ex.InnerException, Is.Not.Null, "원인을 보존해야 진단할 수 있습니다");
        }

        [Test]
        public void PullChanges_AddsNoGuidance_WhenTheRemoteIsALocalPath()
        {
            // 로컬 경로 원격이 사라진 상황. 안내를 붙일 결정적 근거가 없으므로 원문이 그대로 나와야 한다.
            var originPath = NewRepoWithCommit();
            var localPath = NewTempDir();
            Repository.Clone(originPath, localPath);
            TryDeleteDirectory(originPath);

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<LibGit2SharpException>(() => git.PullChanges("localhost", "testdb"),
                "안내가 없으면 원본 예외가 그대로 전파되어야 합니다 - 무관한 오류를 엉뚱한 메시지로 삼키면 안 됩니다");

            Assert.That(ex!.Message, Does.Not.Contain("SSH"));
            Assert.That(ex.Message, Does.Not.Contain("공개키"));
        }

        [Test]
        public void PullChanges_ThrowsLibGit2SharpException_NotArgumentNullException_WhenTheBranchTracksALocalBranch()
        {
            // branch.<name>.remote = "." 는 브랜치가 원격이 아니라 로컬 브랜치를 추적하는 상태다
            // (`git branch --track feature main`이나 autoSetupMerge = always로 만들어진다).
            // 이때 repo.Head.IsTracking은 true이지만 repo.Head.RemoteName은 ""이므로,
            // Remotes[""]를 그대로 색인하면 ArgumentNullException이 터진다. 추적 브랜치 가드는
            // 이 상태를 걸러내지 못한다 - IsTracking이 true이기 때문이다. 가드를 되돌리면
            // (remoteUrl을 다시 repo.Network.Remotes[repo.Head.RemoteName].Url로 직접 색인하면)
            // 이 테스트가 ArgumentNullException으로 실패해야 한다.
            var originPath = NewRepoWithCommit();
            var localPath = NewRepoWithCommit();

            string branchName;
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", originPath);
                branchName = local.Head.FriendlyName;
                local.Config.Set($"branch.{branchName}.remote", ".");
                local.Config.Set($"branch.{branchName}.merge", $"refs/heads/{branchName}");
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<LibGit2SharpException>(() => git.PullChanges("localhost", "testdb"),
                "이전에는 '원격이 없거나 추적 브랜치가 없다'는 영문 libgit2 예외였다. " +
                "이 커밋 이후 회귀로 ArgumentNullException이 대신 나오면 안 된다.");

            Assert.That(ex, Is.Not.InstanceOf<ArgumentNullException>());
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

        // ---------- HasCommitsToPush ----------

        [Test]
        public void HasCommitsToPush_ReturnsTrue_WhenLocalIsAheadOfRemote()
        {
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            var git = NewGitManager("ServerA", "DB1", localPath);

            // 클론 직후에는 앞선 커밋이 없다
            Assert.That(git.HasCommitsToPush("ServerA", "DB1"), Is.False);

            // 새 커밋을 만들면 앞선다
            CommitOneFile(localPath, "test.sql", "select 1", "new commit");
            Assert.That(git.HasCommitsToPush("ServerA", "DB1"), Is.True);
        }

        [Test]
        public void HasCommitsToPush_ReturnsFalse_WhenNoRemoteOrMapping()
        {
            var repoPath = NewRepoWithCommit();
            var git = NewGitManager("ServerA", "DB1", repoPath);

            // 매핑은 있지만 원격이 없으므로 false
            Assert.That(git.HasCommitsToPush("ServerA", "DB1"), Is.False);
            
            // 매핑이 없으면 false
            Assert.That(git.HasCommitsToPush("ServerB", "DB2"), Is.False);
        }

        // ---------- PushChanges ----------

        [Test]
        public void PushChanges_ReturnsNoMapping_WhenDatabaseIsNotMapped()
        {
            var configPath = Path.Combine(NewTempDir(), "mappings.json");
            var git = new GitManager(new ConfigManager(configPath));

            Assert.That(git.PushChanges("localhost", "testdb"), Is.EqualTo(PushResult.NoMapping));
        }

        [Test]
        public void PushChanges_ExplainsInKorean_WhenTheRepositoryHasNoRemote()
        {
            var localPath = NewRepoWithCommit();
            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("원격"));
            Assert.That(ex.Message, Does.Contain("Push할 수 없습니다"),
                "어떤 연산이 막혔는지 이름으로 말해야 합니다");
        }

        [Test]
        public void PushChanges_ExplainsInKorean_WhenTheCurrentBranchHasNoUpstream()
        {
            // git init한 폴더를 매핑하면 실제로 나오는 상태다. 추적을 대신 설정하지 않고 안내만 한다.
            var originPath = NewRepoWithCommit();
            var localPath = NewRepoWithCommit();

            // 기본 브랜치 이름을 하드코딩하면 안 된다. init.defaultBranch가 설정되지 않은 환경
            // (GitHub Actions 러너 등)에서는 master가 되어 개발 기계에서만 통과하는 테스트가 된다.
            string branchName;
            using (var local = new Repository(localPath))
            {
                local.Network.Remotes.Add("origin", originPath);
                branchName = local.Head.FriendlyName;
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<InvalidOperationException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("추적"));
            Assert.That(ex.Message, Does.Contain($"git push -u origin {branchName}"),
                "사용자가 그대로 실행할 수 있는 명령을 줘야 합니다");
        }

        [Test]
        public void PushChanges_ReturnsNothingToPush_WhenTheRemoteIsAlreadyUpToDate()
        {
            var (localPath, _) = NewClonedRepoWithBareOrigin();
            var git = NewGitManager("localhost", "testdb", localPath);

            Assert.That(git.PushChanges("localhost", "testdb"), Is.EqualTo(PushResult.NothingToPush));
        }

        [Test]
        public void PushChanges_UpdatesTheRemoteTip_WhenTheLocalBranchIsAhead()
        {
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            var localSha = CommitOneFile(localPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);", "local change");
            var git = NewGitManager("localhost", "testdb", localPath);

            var result = git.PushChanges("localhost", "testdb");

            Assert.That(result, Is.EqualTo(PushResult.Pushed));
            using var origin = new Repository(originPath);
            // 반환값만 보면 push가 아무것도 하지 않아도 통과한다. 원격의 tip을 직접 확인한다.
            Assert.That(origin.Head.Tip.Sha, Is.EqualTo(localSha),
                "Push 후 원격의 tip이 로컬 커밋이어야 합니다");
        }

        [Test]
        public void PushChanges_ThrowsGitPushRejectedException_WhenTheRemoteHasMovedAhead()
        {
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();

            // 다른 사람이 원격에 먼저 올린다.
            var otherPath = NewTempDir();
            Repository.Clone(originPath, otherPath);
            CommitOneFile(otherPath, "dbo/Tables/Other.sql", "CREATE TABLE Other (Id INT);", "other change");
            using (var other = new Repository(otherPath))
            {
                other.Network.Push(other.Head);
            }

            // 우리는 fetch하지 않은 채 우리 커밋을 만든다.
            var localSha = CommitOneFile(localPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);", "local change");
            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<GitPushRejectedException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("거부"));
            Assert.That(ex.Message, Does.Contain("Pull"),
                "무엇을 해야 하는지 알려줘야 합니다");
            Assert.That(ex.Message, Does.Contain("권한"),
                "브랜치 보호·권한도 같은 증상을 내므로 후보로 남겨야 합니다");

            using var local = new Repository(localPath);
            Assert.That(local.Head.Tip.Sha, Is.EqualTo(localSha),
                "Push는 실패해도 로컬 저장소를 변경하지 않아야 합니다");
        }

        [Test]
        public void PushChanges_ThrowsGitRemoteException_WhenTheHttpsRemoteRefusesTheConnection()
        {
            // 이름 주의: 도달 불가능한 HTTPS 원격이라 connect() 단계에서 거부되고 끝난다 -
            // 자격 증명 콜백이 아예 불리지 않으므로 guidance != null 분기(GitRemoteException)만
            // 지킨다. requiresUserCredentials 분기(GitAuthenticationException)는
            // PushChanges_ThrowsGitAuthenticationException_WhenTheRemoteChallengesWithBasicAuth가
            // 별도로 지킨다 - 이 테스트의 이전 이름(TellsTheUserToSwitchToSsh)은 그 구분을
            // 가리고 있었다.
            var localPath = NewRepoWithCommit();
            string branchName;
            using (var local = new Repository(localPath))
            {
                // 닿지 않는 HTTPS 원격. 접속을 시도하기 전에 판정되는 안내만 확인한다.
                local.Network.Remotes.Add("origin", "https://127.0.0.1:1/nope.git");
                branchName = local.Head.FriendlyName;
                var branch = local.Branches[branchName];
                local.Branches.Update(branch,
                    b => b.Remote = "origin",
                    b => b.UpstreamBranch = $"refs/heads/{branchName}");
            }

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<GitRemoteException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("SSH 원격으로 바꾸세요"));
            Assert.That(ex.InnerException, Is.Not.Null, "원인을 보존해야 진단할 수 있습니다");
        }

        [Test]
        public void PushChanges_AddsNoGuidance_WhenTheRemoteIsALocalPath()
        {
            // RemoteDiagnostics가 Other/Unknown에 null을 주므로 무관한 실패에 힌트가 붙지 않아야 한다.
            var (localPath, originPath) = NewClonedRepoWithBareOrigin();
            CommitOneFile(localPath, "dbo/Tables/Orders.sql", "CREATE TABLE Orders (Id INT);", "local change");
            TryDeleteDirectory(originPath);

            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<LibGit2SharpException>(() => git.PushChanges("localhost", "testdb"),
                "안내할 것이 없으면 libgit2의 원본 예외가 그대로 전파돼야 합니다");
            Assert.That(ex!.Message, Does.Not.Contain("SSH"));
        }

        [Test]
        // 자동 실행에서 제외한다. PullChanges의 동일한 테스트(아래)가 Windows net48에서 겪은 것과
        // 같은 문제다 - HTTP.sys를 통한 인증 왕복이 걸리면서 CI 잡 전체를 무기한 멈추게 한 전례가
        // 있다. 그래서 그 테스트도 수동 실행 전용으로 남아 있고, 여기도 같은 이유로 맞춘다.
        [Explicit("Windows net48에서 무한 대기한다. 수동 실행 전용.")]
        public void PushChanges_ThrowsGitAuthenticationException_WhenTheRemoteChallengesWithBasicAuth()
        {
            // catch (LibGit2SharpException ex) when (requiresUserCredentials) 분기는 지금까지
            // 어떤 테스트도 지나지 않았다 - PushChanges_ThrowsGitRemoteException_WhenTheHttpsRemoteRefusesTheConnection은
            // 자격 증명 콜백 이전(connect 단계)에서 실패해서 이 분기를 건드리지 못한다.
            // 단위 테스트로 격리된 BuildPushOptions/ResolveCredentials가 옳아도, PushChanges가
            // 그 옵션을 실제로 Network.Push에 넘기지 않으면 이 경로는 절대 실행되지 않는다.
            // PullChanges_ThrowsGitAuthenticationException_WhenTheRemoteChallengesWithBasicAuth와
            // 같은 방식으로, Basic 인증을 요구하는 실제 HTTP 서버를 띄워 PushChanges 전체 경로
            // (빌드된 PushOptions -> Network.Push -> 자격 증명 콜백 호출 ->
            // requiresUserCredentials 전파 -> GitAuthenticationException 변환)를
            // end-to-end로 검증한다.
            using var server = new BasicAuthChallengeServer();
            var localPath = NewRepoWithCommit();
            using (var repo = new Repository(localPath))
            {
                repo.Network.Remotes.Add("origin", server.Url);
                // Network.Push는 현재 브랜치에 추적 정보(remote/merge)가 있어야 동작한다.
                // 실제 원격에서 통신할 수 없으므로(서버가 401만 준다) 수동으로 설정한다.
                var branchName = repo.Head.FriendlyName;
                repo.Config.Set($"branch.{branchName}.remote", "origin");
                repo.Config.Set($"branch.{branchName}.merge", $"refs/heads/{branchName}");
            }
            var git = NewGitManager("localhost", "testdb", localPath);

            var ex = Assert.Throws<GitAuthenticationException>(() => git.PushChanges("localhost", "testdb"));

            Assert.That(ex!.Message, Does.Contain("자격 증명"),
                "GitManager.ResolveCredentials가 실제로 Network.Push의 CredentialsProvider로 호출됐어야 이 경로에 도달합니다");
        }

        // ---------- BuildPushOptions (콜백 배선) ----------

        [Test]
        public void BuildPushOptions_WiresResolveCredentialsIntoTheCredentialsProvider()
        {
            // Pull과 같은 이유다. ResolveCredentials가 단위 테스트를 통과하는 것과, 그것이 실제로
            // PushChanges가 쓰는 PushOptions에 연결되어 있는 것은 별개다. 파일 경로 원격을 쓰는
            // 다른 Push 테스트는 자격 증명 콜백을 아예 거치지 않으므로 이 배선을 지키지 못한다.
            var options = GitManager.BuildPushOptions(() => { }, _ => { });

            Assert.That(options.CredentialsProvider, Is.Not.Null,
                "CredentialsProvider가 비어 있으면 인증이 필요한 원격에서 항상 실패합니다");
        }

        [Test]
        public void BuildPushOptions_InvokesTheCredentialsCallback_OnlyWhenTheRemoteRequiresUserCredentials()
        {
            var requiresUserCredentialsCallCount = 0;
            var options = GitManager.BuildPushOptions(() => requiresUserCredentialsCallCount++, _ => { });

            // Default를 지원하는 원격: 통합 인증으로 처리되므로 콜백이 불리면 안 된다.
            options.CredentialsProvider!("https://example.com/repo.git", null, SupportedCredentialTypes.Default);
            Assert.That(requiresUserCredentialsCallCount, Is.Zero);

            // Default를 지원하지 않는 원격: 콜백이 불려야 PushChanges가 GitAuthenticationException으로 감쌀 수 있다.
            options.CredentialsProvider!("https://example.com/repo.git", null, SupportedCredentialTypes.UsernamePassword);
            Assert.That(requiresUserCredentialsCallCount, Is.EqualTo(1));
        }

        [Test]
        public void BuildPushOptions_WiresOnPushStatusError()
        {
            // 이 배선이 없으면 서버가 ref 갱신을 거부해도 Network.Push가 정상 반환한다.
            // 즉 실패가 성공으로 보고된다. 단위 테스트가 닿는 유일한 지점이므로 여기서 지킨다.
            //
            // 호출 횟수만 세면 `OnPushStatusError = error => onPushStatusError(null)`처럼
            // 인스턴스를 떨어뜨려도 통과한다 - 정확히 이 테스트가 막으려는 결함이다.
            // 전달된 인스턴스가 그대로 넘어오는지까지 확인한다.
            PushStatusError? received = null;
            var options = GitManager.BuildPushOptions(() => { }, error => received = error);

            Assert.That(options.OnPushStatusError, Is.Not.Null);
            var error = new FakePushStatusError("refs/heads/main", "rejected");
            options.OnPushStatusError!(error);
            Assert.That(received, Is.SameAs(error),
                "콜백이 다른 인스턴스를(또는 null을) 넘기면 서버의 실제 거부 사유가 조용히 사라집니다");
        }

        // ---------- BuildPushRejectionMessage (거부 안내 문구) ----------

        [Test]
        public void BuildPushRejectionMessage_UsesAGenericHeader_WhenNoStatusErrorIsGiven()
        {
            // NonFastForwardException 경로(로컬/파일 전송)다 - 서버 원문이 없으므로 일반 문구만 싣는다.
            var message = GitManager.BuildPushRejectionMessage(null);

            Assert.That(message, Does.Contain("원격이 Push를 거부했습니다."));
            Assert.That(message, Does.Contain("Pull을 먼저 하세요"));
            Assert.That(message, Does.Contain("권한"));
        }

        [Test]
        public void BuildPushRejectionMessage_IncludesTheServersReferenceAndMessage_WhenAStatusErrorIsGiven()
        {
            // OnPushStatusError 경로(smart 전송 - SSH·HTTPS)다. 파일 기반 전송으로는 이 콜백이
            // 호출되는 상황 자체를 재현할 수 없으므로(non-bare 대상은 상태 오류 없이
            // BareRepositoryException을 던진다), 이 문구 조립은 테스트 이중체로만 검증할 수 있다.
            // 실제 smart 전송을 통한 end-to-end 검증은 여전히 커버되지 않는다 - 알려진 한계다.
            var error = new FakePushStatusError("refs/heads/main", "protected branch hook declined");

            var message = GitManager.BuildPushRejectionMessage(error);

            Assert.That(message, Does.Contain("원격이 'refs/heads/main' 갱신을 거부했습니다."));
            Assert.That(message, Does.Contain("서버 응답: protected branch hook declined"));
            Assert.That(message, Does.Contain("Pull을 먼저 하세요"));
            Assert.That(message, Does.Contain("권한"));
        }

        // ---------- Clone ----------

        /// <summary>
        /// 보고를 그 자리에서 모은다. Progress&lt;T&gt;는 생성된 스레드의 SynchronizationContext로
        /// 넘기는데, 테스트 스레드에는 그것이 없어 순서와 시점이 보장되지 않는다.
        /// </summary>
        private sealed class RecordingProgress<T> : IProgress<T>
        {
            private readonly Action<T>? _onReport;
            public RecordingProgress(Action<T>? onReport = null) { _onReport = onReport; }
            public System.Collections.Generic.List<T> Reports { get; } = new System.Collections.Generic.List<T>();
            public void Report(T value) { Reports.Add(value); _onReport?.Invoke(value); }
        }

        [Test]
        public void CloneRepository_CreatesAWorkingTreeAtTheTargetPath_WhenTheRemoteIsReachable()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();

            var result = new GitManager().CloneRepository(originPath, targetPath, null, CancellationToken.None);

            // Repository.Clone이 돌려주는 것은 .git 디렉터리 경로다. 그것을 그대로 매핑에 넣으면
            // 이후 모든 동작이 어긋나므로, 반환값은 작업 트리여야 한다.
            Assert.That(result, Is.EqualTo(targetPath));
            Assert.That(File.Exists(Path.Combine(targetPath, "dbo", "Tables", "Users.sql")), Is.True);
            Assert.That(new GitManager().IsRepository(targetPath), Is.True);
        }

        [Test]
        public void CloneRepository_SetsUpstreamTracking_WhenCloning()
        {
            // clone이 Init+Remote+upstream을 대신하는 근거다. 이것이 깨지면 첫 Push가
            // "추적 중인 원격 브랜치가 없어" 로 거부된다.
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();

            new GitManager().CloneRepository(originPath, targetPath, null, CancellationToken.None);

            using var cloned = new Repository(targetPath);
            Assert.That(cloned.Head.IsTracking, Is.True);
        }

        [Test]
        public void CloneRepository_ReportsProgress_WhileCloning()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();
            var progress = new RecordingProgress<CloneProgress>();

            new GitManager().CloneRepository(originPath, targetPath, progress, CancellationToken.None);

            // 파일 경로 원격은 libgit2의 local 전송을 타서 전송 단계 보고가 없을 수 있다.
            // 그래서 여기서 고정하는 것은 checkout 보고뿐이고, 전송 보고는 실기 확인 목록에 있다.
            Assert.That(progress.Reports, Is.Not.Empty);
            Assert.That(progress.Reports.Exists(p => p.Phase == ClonePhase.CheckingOut), Is.True);
        }

        [Test]
        public void CloneRepository_Refuses_WhenTheTargetFolderAlreadyExists()
        {
            var originPath = NewRepoWithCommit();
            var targetPath = NewTempDir();
            Directory.CreateDirectory(targetPath);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new GitManager().CloneRepository(originPath, targetPath, null, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain(targetPath),
                "어느 폴더가 문제인지 경로로 알려줘야 합니다");
            Assert.That(ex.Message, Does.Contain("이미"));
        }

        [Test]
        public void CloneRepository_RefusesBeforeCreatingAnything_WhenTheRemoteIsHttps()
        {
            var targetPath = NewTempDir();

            var ex = Assert.Throws<GitAuthenticationException>(
                () => new GitManager().CloneRepository(
                    "https://example.invalid/org/x.git", targetPath, null, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("SSH"),
                "HTTPS 원격에는 SSH로 바꾸는 방법을 안내해야 합니다");
            Assert.That(Directory.Exists(targetPath), Is.False,
                "네트워크를 타기 전에 거부해야 합니다 - 폴더가 남으면 다음 시도가 '이미 있음'으로 막힙니다");
        }

        [Test]
        public void CloneRepository_Refuses_WhenTheRemoteUrlIsEmpty()
        {
            Assert.Throws<ArgumentException>(
                () => new GitManager().CloneRepository("  ", NewTempDir(), null, CancellationToken.None));
        }

        /// <summary>
        /// <see cref="PushStatusError"/>의 기본 생성자는 protected이고 <c>Reference</c>·<c>Message</c>는
        /// virtual get-only 프로퍼티다(리플렉션으로 확인함 - 두 프로퍼티 모두 setter가 없다).
        /// 실제 SSH/HTTPS 전송 없이 <see cref="GitManager.BuildPushRejectionMessage"/>가 만드는
        /// 사용자 문구를 검증하기 위한 최소 이중체다.
        /// </summary>
        private sealed class FakePushStatusError : PushStatusError
        {
            public FakePushStatusError(string reference, string message)
            {
                Reference = reference;
                Message = message;
            }

            public override string Reference { get; }

            public override string Message { get; }
        }

        [Test]
        public void GitPushRejectedException_CarriesTheInnerException()
        {
            var inner = new InvalidOperationException("원본");
            var ex = new GitPushRejectedException("거부", inner);

            Assert.That(ex.Message, Is.EqualTo("거부"));
            Assert.That(ex.InnerException, Is.SameAs(inner));
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
        // 자동 실행에서 제외한다. macOS와 Linux(net10.0)에서는 통과하지만 Windows의 net48에서
        // 무한 대기해 CI 잡 전체를 멈춰 세웠다(실측: 러너가 1시간 넘게 이 단계에 머물렀다).
        // Windows에서는 네이티브 전송이 WinHTTP를 타고 HttpListener가 HTTP.sys를 거치는데,
        // LibGit2Sharp 0.32는 연결/읽기 타임아웃을 노출하지 않는다.
        // [CancelAfter]로 막으려 했으나 블로킹 중인 네이티브 호출은 중단시키지 못한다 - 실측으로 반증됐다.
        //
        // 이 테스트가 지키는 것: 격리된 BuildPullOptions/ResolveCredentials가 옳아도 PullChanges가
        // 그 옵션을 Commands.Pull에 넘기지 않으면 자격 증명 경로는 죽은 코드가 된다. 그 배선을
        // 검증하는 유일한 테스트다. 지금은 `dotnet test --filter` 로 수동 실행해야 하며,
        // GitHub·GitLab 대응으로 자격 증명 설계를 다시 할 때 CI에서 돌릴 방법도 함께 설계한다.
        [Explicit("Windows net48에서 무한 대기한다. 수동 실행 전용.")]
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
