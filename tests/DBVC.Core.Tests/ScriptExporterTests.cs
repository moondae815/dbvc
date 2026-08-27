using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class ScriptExporterTests
    {
        private const string Server = "localhost";
        private const string Database = "testdb";
        private static readonly DateTimeOffset GeneratedAt = new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.Zero);
        private static readonly Signature TestSignature = new Signature("Test", "test@example.com", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        private readonly List<string> _tempDirs = new List<string>();
        private string _repoPath = null!;
        private ConfigManager _config = null!;
        private GitManager _git = null!;

        [SetUp]
        public void SetUp()
        {
            _repoPath = NewTempDir();
            Repository.Init(_repoPath);

            _config = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            _config.AddMapping(Server, Database, _repoPath);
            _git = new GitManager(_config);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_export_" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(path);
            return path;
        }

        private void WriteFile(string relativePath, string content)
        {
            var full = Path.Combine(_repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        private void CommitAll(string message)
        {
            using var repo = new Repository(_repoPath);
            Commands.Stage(repo, "*");
            repo.Commit(message, TestSignature, TestSignature);
        }

        private static ChangeRecord Target(string qualifiedName, string relativePath)
            => new ChangeRecord { QualifiedName = qualifiedName, RelativePath = relativePath };

        private ScriptExporter NewExporter() => new ScriptExporter(_config, _git);

        // ---------- Deployment ----------

        [Test]
        public void Export_Deployment_UsesTheCurrentWorkingTreeContent()
        {
            WriteFile("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);");
            CommitAll("initial");
            WriteFile("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");

            var result = NewExporter().Export(
                Server, Database,
                new[] { Target("dbo.Users", "dbo/Tables/Users.sql") },
                ScriptKind.Deployment, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(1));
            Assert.That(result.Script, Does.Contain("Name NVARCHAR(50)"),
                "Deployment는 커밋된 버전이 아니라 현재 DB 기준 최신 코드를 담아야 합니다");
            Assert.That(result.ExcludedObjects, Is.Empty);
        }

        [Test]
        public void Export_Deployment_ExcludesObjectsWhoseFileIsMissing()
        {
            WriteFile("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);");

            var result = NewExporter().Export(
                Server, Database,
                new[]
                {
                    Target("dbo.Users", "dbo/Tables/Users.sql"),
                    Target("dbo.Gone", "dbo/Tables/Gone.sql")
                },
                ScriptKind.Deployment, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(1));
            Assert.That(result.ExcludedObjects.Select(e => e.QualifiedName), Is.EqualTo(new[] { "dbo.Gone" }));
            Assert.That(result.Script, Does.Not.Contain("/* ---- dbo.Gone"),
                "제외된 객체의 본문 섹션은 들어가면 안 됩니다 - 원래 이 단언이 지키려던 것입니다");
            Assert.That(result.Script, Does.Contain("제외 — 스크립트로 만들 내용이 없습니다: 1 (dbo.Gone)"),
                "다만 무엇이 빠졌는지는 헤더에 남아야 합니다. ScriptExporter가 제외 목록을 전달하지 않으면 실패합니다");
        }

        // ---------- Rollback ----------

        [Test]
        public void Export_Rollback_UsesTheRevisionBeforeTheLastCommit()
        {
            WriteFile("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);");
            CommitAll("initial");
            WriteFile("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT, Name NVARCHAR(50));");
            CommitAll("second");

            var result = NewExporter().Export(
                Server, Database,
                new[] { Target("dbo.Users", "dbo/Tables/Users.sql") },
                ScriptKind.Rollback, GeneratedAt);

            Assert.That(result.Script, Does.Contain("CREATE TABLE Users (Id INT);"));
            Assert.That(result.Script, Does.Not.Contain("Name NVARCHAR(50)"));
        }

        [Test]
        public void Export_Rollback_ExcludesObjectsThatHaveNoEarlierRevision()
        {
            WriteFile("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);");
            CommitAll("initial");

            var result = NewExporter().Export(
                Server, Database,
                new[] { Target("dbo.Users", "dbo/Tables/Users.sql") },
                ScriptKind.Rollback, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(0));
            Assert.That(result.ExcludedObjects.Select(e => e.QualifiedName), Is.EqualTo(new[] { "dbo.Users" }));
            Assert.That(result.HasContent, Is.False, "포함된 객체가 없으면 파일을 만들 이유가 없습니다");
        }

        // ---------- 공통 ----------

        [Test]
        public void Export_ReturnsNoContent_WhenDatabaseIsNotMapped()
        {
            var emptyConfig = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            var exporter = new ScriptExporter(emptyConfig, new GitManager(emptyConfig));

            var result = exporter.Export(
                Server, Database,
                new[] { Target("dbo.Users", "dbo/Tables/Users.sql") },
                ScriptKind.Deployment, GeneratedAt);

            Assert.That(result.HasContent, Is.False);
            Assert.That(result.IncludedCount, Is.EqualTo(0));
        }

        [Test]
        public void Export_ReturnsNoContent_ForAnEmptyTargetList()
        {
            var result = NewExporter().Export(
                Server, Database, Array.Empty<ChangeRecord>(), ScriptKind.Deployment, GeneratedAt);

            Assert.That(result.HasContent, Is.False);
        }

        [Test]
        public void Export_MergesMultipleObjectsIntoASingleScript()
        {
            WriteFile("dbo/Tables/Users.sql", "CREATE TABLE Users (Id INT);");
            WriteFile("dbo/StoredProcedures/usp_Get.sql", "CREATE PROCEDURE usp_Get AS SELECT 1;");

            var result = NewExporter().Export(
                Server, Database,
                new[]
                {
                    Target("dbo.usp_Get", "dbo/StoredProcedures/usp_Get.sql"),
                    Target("dbo.Users", "dbo/Tables/Users.sql")
                },
                ScriptKind.Deployment, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(2));
            Assert.That(result.Script.IndexOf("dbo.Users", StringComparison.Ordinal),
                Is.LessThan(result.Script.IndexOf("dbo.usp_Get", StringComparison.Ordinal)),
                "테이블이 프로시저보다 앞에 와야 합니다");
        }

        // ---------- ExportFromComparison: 차이 목록이 곧 분류의 입력이다 ----------

        private void WriteRepositoryFile(string relativePath, string content)
        {
            var full = Path.Combine(_repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        [Test]
        public void ExportFromComparison_IncludesNewObjectsAndModifiedProcedures()
        {
            WriteRepositoryFile("dbo/Tables/Orders.sql", "CREATE TABLE dbo.Orders (Id INT)");
            WriteRepositoryFile("dbo/StoredProcedures/GetUser.sql", "CREATE OR ALTER PROCEDURE dbo.GetUser AS SELECT 1");

            var differences = new[]
            {
                new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.MissingInDatabase),
                new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(2));
            Assert.That(result.ExcludedObjects, Is.Empty);
            Assert.That(result.Script, Does.Contain("CREATE TABLE dbo.Orders"));
            Assert.That(result.Script, Does.Contain("CREATE OR ALTER PROCEDURE dbo.GetUser"));
        }

        [Test]
        public void ExportFromComparison_NamesUnjudgedObjectsInTheHeader()
        {
            // 나중에 이 .sql만 열어 본 DBA에게는 문서가 비교 전체를 덮는 것처럼 보인다.
            // 판정하지 못한 객체를 적지 않으면 그 주장이 거짓이 된다 - 화면의 알림은
            // 파일과 함께 남지 않는다.
            WriteRepositoryFile("dbo/Tables/Orders.sql", "CREATE TABLE dbo.Orders (Id INT)");

            var differences = new[]
            {
                new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.MissingInDatabase)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(
                Server, Database, differences, GeneratedAt, new[] { "dbo.Encrypted" });

            Assert.That(result.IncludedCount, Is.EqualTo(1));
            Assert.That(result.ExcludedObjects.Count, Is.EqualTo(1));
            Assert.That(result.ExcludedObjects[0].Reason, Is.EqualTo(ScriptExclusionReason.NotCompared));
            Assert.That(result.Script, Does.Contain("dbo.Encrypted"));
            Assert.That(result.Script, Does.Contain("판정하지 못했습니다"));
        }

        [Test]
        public void ExportFromComparison_ExcludesModifiedTable_AsManualChange()
        {
            WriteRepositoryFile("dbo/Tables/Orders.sql", "CREATE TABLE dbo.Orders (Id INT)");

            var differences = new[]
            {
                new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.Modified)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.IncludedCount, Is.EqualTo(0));
            Assert.That(result.ExcludedObjects.Count, Is.EqualTo(1));
            Assert.That(result.ExcludedObjects[0].QualifiedName, Is.EqualTo("dbo.Orders"));
            Assert.That(result.ExcludedObjects[0].Reason, Is.EqualTo(ScriptExclusionReason.ManualChangeRequired));
            Assert.That(result.HasContent, Is.False);
        }

        [Test]
        public void ExportFromComparison_ExcludesDatabaseOnlyObject_AsNotInBranch()
        {
            var differences = new[]
            {
                new SchemaDifference("dbo.Temp1", "dbo/Tables/Temp1.sql", "Table", ObjectDiffState.MissingInBranch)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.ExcludedObjects[0].Reason, Is.EqualTo(ScriptExclusionReason.NotInBranch));
        }

        [Test]
        public void ExportFromComparison_ExcludesAsNoContent_WhenBranchFileIsMissing()
        {
            // 차이 목록은 파일이 있다고 말했는데 실제로 없다. 검사와 생성 사이에 누군가
            // 지웠거나 권한이 막은 것이다. 조용히 빼면 배포가 덜 된 채로 성공한 척한다.
            var differences = new[]
            {
                new SchemaDifference("dbo.Gone", "dbo/Views/Gone.sql", "View", ObjectDiffState.MissingInDatabase)
            };

            var exporter = new ScriptExporter(_config, _git);
            var result = exporter.ExportFromComparison(Server, Database, differences, GeneratedAt);

            Assert.That(result.ExcludedObjects[0].Reason, Is.EqualTo(ScriptExclusionReason.NoContent));
        }

        [Test]
        public void ExportFromComparison_ReturnsEmpty_WhenThereIsNoMapping()
        {
            var emptyConfig = new ConfigManager(Path.Combine(NewTempDir(), "mappings.json"));
            var exporter = new ScriptExporter(emptyConfig, new GitManager(emptyConfig));

            var result = exporter.ExportFromComparison(Server, Database, new SchemaDifference[0], GeneratedAt);

            Assert.That(result.HasContent, Is.False);
        }
    }
}
