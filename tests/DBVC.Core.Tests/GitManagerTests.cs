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
    }
}
