using System;
using System.IO;
using System.Linq;
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
