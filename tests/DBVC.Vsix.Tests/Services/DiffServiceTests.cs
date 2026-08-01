using System;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class DiffServiceTests
    {
        private const string Server = "LocalServer";
        private const string Database = "SalesDB";
        private const string RelativePath = "dbo/Tables/Users.sql";

        private string _repoPath = null!;
        private Mock<IConfigManager> _config = null!;
        private Mock<IGitManager> _git = null!;

        [SetUp]
        public void SetUp()
        {
            _repoPath = Path.Combine(Path.GetTempPath(), "dbvc_diff_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_repoPath);

            _config = new Mock<IConfigManager>();
            _config.Setup(c => c.TryGetMapping(Server, Database))
                .Returns(new MappingConfig { ServerName = Server, DatabaseName = Database, GitPath = _repoPath });

            _git = new Mock<IGitManager>();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_repoPath))
            {
                try { Directory.Delete(_repoPath, true); } catch { }
            }
        }

        private DiffService NewDiffService() => new DiffService(_config.Object, _git.Object);

        private void WriteWorkingTreeFile(string content)
        {
            var full = Path.Combine(_repoPath, "dbo", "Tables", "Users.sql");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        // ---------- 문자열 Diff ----------

        [Test]
        public void GetDiffModelFromString_ReturnsModel()
        {
            var model = new DiffService().GetDiffModelFromString("A", "B");

            Assert.That(model, Is.Not.Null);
            Assert.That(model.OldText.Lines.Count, Is.EqualTo(1));
        }

        [Test]
        public void GetDiffModelFromString_HandlesNullsGracefully()
        {
            Assert.That(new DiffService().GetDiffModelFromString(null, null), Is.Not.Null);
        }

        // ---------- 객체 Diff ----------

        [Test]
        public void GetDiffModel_PutsGitHeadVersionOnLeftAndCurrentDatabaseVersionOnRight()
        {
            _git.Setup(g => g.GetFileContentAtHead(Server, Database, RelativePath))
                .Returns("CREATE TABLE Users (Id INT);");
            WriteWorkingTreeFile("CREATE TABLE Users (Id INT, Name NVARCHAR(50));");

            var model = NewDiffService().GetDiffModel(Server, Database, RelativePath);

            Assert.That(string.Join("\n", model.OldText.Lines.Select(l => l.Text)),
                Does.Contain("CREATE TABLE Users (Id INT);"));
            Assert.That(string.Join("\n", model.NewText.Lines.Select(l => l.Text)),
                Does.Contain("Name NVARCHAR(50)"));
        }

        [Test]
        public void GetDiffModel_LeavesLeftSideEmpty_ForNewObjectAbsentFromGit()
        {
            // 설계: "new objects will simply show empty left side and full right side"
            _git.Setup(g => g.GetFileContentAtHead(Server, Database, RelativePath)).Returns((string?)null);
            WriteWorkingTreeFile("CREATE TABLE Users (Id INT);");

            var model = NewDiffService().GetDiffModel(Server, Database, RelativePath);

            Assert.That(model.OldText.Lines.All(l => string.IsNullOrEmpty(l.Text)), Is.True);
            Assert.That(string.Join("\n", model.NewText.Lines.Select(l => l.Text)), Does.Contain("CREATE TABLE Users"));
        }

        [Test]
        public void GetDiffModel_LeavesRightSideEmpty_ForDeletedObject()
        {
            // 객체가 DROP되면 작업 트리에 파일이 없다.
            _git.Setup(g => g.GetFileContentAtHead(Server, Database, RelativePath))
                .Returns("CREATE TABLE Users (Id INT);");

            var model = NewDiffService().GetDiffModel(Server, Database, RelativePath);

            Assert.That(string.Join("\n", model.OldText.Lines.Select(l => l.Text)), Does.Contain("CREATE TABLE Users"));
            Assert.That(model.NewText.Lines.All(l => string.IsNullOrEmpty(l.Text)), Is.True);
        }

        [Test]
        public void GetDiffModel_ReturnsEmptyModel_WhenDatabaseIsNotMapped()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);

            var model = NewDiffService().GetDiffModel(Server, Database, RelativePath);

            Assert.That(model, Is.Not.Null);
            Assert.That(model.OldText.Lines.All(l => string.IsNullOrEmpty(l.Text)), Is.True);
            Assert.That(model.NewText.Lines.All(l => string.IsNullOrEmpty(l.Text)), Is.True);
        }

        [Test]
        public void GetDiffModel_ReturnsEmptyModel_WhenRelativePathIsMissing()
        {
            var model = NewDiffService().GetDiffModel(Server, Database, null);

            Assert.That(model, Is.Not.Null);
            _git.Verify(g => g.GetFileContentAtHead(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ---------- 원문 조회(에디터 렌더링용) ----------

        [Test]
        public void GetDiffTexts_ReturnsBothSidesAsPlainText()
        {
            _git.Setup(g => g.GetFileContentAtHead(Server, Database, RelativePath)).Returns("old");
            WriteWorkingTreeFile("new");

            var (oldText, newText) = NewDiffService().GetDiffTexts(Server, Database, RelativePath);

            Assert.That(oldText, Is.EqualTo("old"));
            Assert.That(newText, Is.EqualTo("new"));
        }
    }
}
