using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class GitManagerTests
    {
        [Test]
        public void GetStatus_ReturnsStatusForRepo()
        {
            var manager = new GitManager();
            var status = manager.GetStatus("dummy/path");
            Assert.That(status, Is.Not.Null);
            Assert.That(status, Is.EqualTo("Clean"));
        }

        [Test]
        public void GetStatusForDatabase_UsesConfigManagerMapping()
        {
            var configManager = new ConfigManager();
            configManager.AddMapping(new MappingConfig
            {
                ServerName = "LocalServer",
                DatabaseName = "SalesDB",
                GitPath = @"D:\Repositories\SalesRepo"
            });

            var gitManager = new GitManager(configManager);
            var status = gitManager.GetStatusForDatabase("LocalServer", "SalesDB");

            Assert.That(status, Is.Not.Null);
            Assert.That(status, Is.EqualTo("Clean"));
        }

        [Test]
        public void Commit_ReturnsTrue_WhenCommitSucceeds()
        {
            var manager = new GitManager();
            var result = manager.Commit("dummy/path", "dbo/Tables/TestTable.sql", "Initial commit");
            Assert.That(result, Is.True);
        }

        [Test]
        public void Constructor_ThrowsArgumentNullException_WhenConfigManagerIsNull()
        {
            Assert.Throws<System.ArgumentNullException>(() => new GitManager(null!));
        }

        [Test]
        public void CommitChanges_ThrowsException_IfRepoNotFound()
        {
            var config = new ConfigManager();
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbvc_git_test_" + System.Guid.NewGuid().ToString("N"));
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            System.IO.Directory.CreateDirectory(path);

            try
            {
                config.AddMapping(new MappingConfig { ServerName = "localhost", DatabaseName = "testdb", GitPath = path });
                var git = new GitManager(config);

                Assert.Throws<LibGit2Sharp.RepositoryNotFoundException>(() => git.CommitChanges("localhost", "testdb", "test"));
            }
            finally
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
        }

        [Test]
        public void CommitChanges_StagesAndCommitsFiles_WhenRepoExists()
        {
            var config = new ConfigManager();
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbvc_git_test_" + System.Guid.NewGuid().ToString("N"));
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);

            try
            {
                LibGit2Sharp.Repository.Init(path);
                System.IO.File.WriteAllText(System.IO.Path.Combine(path, "test.sql"), "CREATE TABLE Test (Id INT);");

                config.AddMapping(new MappingConfig { ServerName = "localhost", DatabaseName = "testdb", GitPath = path });
                var git = new GitManager(config);

                var result = git.CommitChanges("localhost", "testdb", "Initial schema commit");
                Assert.That(result, Is.True);

                using var repo = new LibGit2Sharp.Repository(path);
                var lastCommit = repo.Head.Tip;
                Assert.That(lastCommit, Is.Not.Null);
                Assert.That(lastCommit.Message.TrimEnd(), Is.EqualTo("Initial schema commit"));
            }
            finally
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
        }

        [Test]
        public void PullChanges_ReturnsTrue()
        {
            var config = new ConfigManager();
            var git = new GitManager(config);
            var result = git.PullChanges("localhost", "testdb");
            Assert.That(result, Is.True);
        }
    }
}
