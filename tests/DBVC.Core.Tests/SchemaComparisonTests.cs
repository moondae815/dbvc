using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 대상 DB를 훑으면 "브랜치에 있는데 DB에 없는 것"은 애초에 열거되지 않는다.
    /// 그것이 배포에서 가장 중요한 항목이므로 저장소 쪽에서 따로 찾아야 한다.
    /// </summary>
    [TestFixture]
    public class SchemaComparisonTests
    {
        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dbvc_cmp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        [Test]
        public void FindMissingInDatabase_ReturnsPathsNotExtracted()
        {
            var repoPaths = new[] { "dbo/Tables/Users.sql", "dbo/StoredProcedures/GetUser.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };

            var missing = SchemaComparison.FindMissingInDatabase(repoPaths, extracted);

            Assert.That(missing.Count, Is.EqualTo(1));
            Assert.That(missing[0].RelativePath, Is.EqualTo("dbo/StoredProcedures/GetUser.sql"));
            Assert.That(missing[0].QualifiedName, Is.EqualTo("dbo.GetUser"));
            Assert.That(missing[0].ObjectType, Is.EqualTo("StoredProcedure"));
            Assert.That(missing[0].State, Is.EqualTo(ObjectDiffState.MissingInDatabase));
        }

        [Test]
        public void FindMissingInDatabase_ReturnsEmpty_WhenEverythingWasExtracted()
        {
            var repoPaths = new[] { "dbo/Tables/Users.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };

            Assert.That(SchemaComparison.FindMissingInDatabase(repoPaths, extracted), Is.Empty);
        }

        [Test]
        public void FindMissingInDatabase_IgnoresCase_WhenMatchingExtractedPaths()
        {
            // Windows 파일 시스템은 대소문자를 구분하지 않는다. 여기서 구분하면 같은 파일이
            // "DB에 없는 객체"로 보고된다.
            var repoPaths = new[] { "DBO/Tables/Users.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo/Tables/Users.sql" };

            Assert.That(SchemaComparison.FindMissingInDatabase(repoPaths, extracted), Is.Empty);
        }

        [Test]
        public void FindMissingInDatabase_SkipsPathsOutsideTheConvention()
        {
            // 저장소에 사람이 둔 잡다한 .sql이 "DB에 없는 객체"로 보고되면 안 된다.
            var repoPaths = new[] { "README.sql", "docs/notes.sql", "dbo/Tables/Users/extra.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Assert.That(SchemaComparison.FindMissingInDatabase(repoPaths, extracted), Is.Empty);
        }

        [Test]
        public void FindMissingInDatabase_NormalizesBackslashes()
        {
            var repoPaths = new[] { @"dbo\Views\ActiveUsers.sql" };
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var missing = SchemaComparison.FindMissingInDatabase(repoPaths, extracted);

            Assert.That(missing.Count, Is.EqualTo(1));
            Assert.That(missing[0].RelativePath, Is.EqualTo("dbo/Views/ActiveUsers.sql"));
        }

        [Test]
        public void FindMissingInDatabase_ReturnsEmpty_WhenInputsAreNull()
        {
            Assert.That(SchemaComparison.FindMissingInDatabase(null, null), Is.Empty);
        }

        [Test]
        public void EnumerateRepositoryScriptPaths_ReturnsSlashSeparatedRelativePaths()
        {
            var root = NewTempDir();
            Directory.CreateDirectory(Path.Combine(root, "dbo", "Tables"));
            File.WriteAllText(Path.Combine(root, "dbo", "Tables", "Users.sql"), "-- t");

            var paths = SchemaComparison.EnumerateRepositoryScriptPaths(root);

            Assert.That(paths, Is.EquivalentTo(new[] { "dbo/Tables/Users.sql" }));
        }

        [Test]
        public void EnumerateRepositoryScriptPaths_SkipsTheGitDirectory()
        {
            // .git 안에도 .sql이 들어갈 수 있다(훅 예제, 사용자가 둔 파일).
            // 그것이 "DB에 없는 객체"로 보고되면 목록이 통째로 신뢰를 잃는다.
            var root = NewTempDir();
            Directory.CreateDirectory(Path.Combine(root, ".git", "hooks"));
            File.WriteAllText(Path.Combine(root, ".git", "hooks", "sample.sql"), "-- x");

            Assert.That(SchemaComparison.EnumerateRepositoryScriptPaths(root), Is.Empty);
        }

        [Test]
        public void EnumerateRepositoryScriptPaths_ReturnsEmpty_WhenDirectoryDoesNotExist()
        {
            var missing = Path.Combine(Path.GetTempPath(), "dbvc_absent_" + Guid.NewGuid().ToString("N"));

            Assert.That(SchemaComparison.EnumerateRepositoryScriptPaths(missing), Is.Empty);
        }

        [Test]
        public void ScanRepositoryScriptPaths_ReportsComplete_WhenTheWholeTreeWasRead()
        {
            var root = NewTempDir();
            Directory.CreateDirectory(Path.Combine(root, "dbo", "Tables"));
            File.WriteAllText(Path.Combine(root, "dbo", "Tables", "Users.sql"), "-- t");

            var scan = SchemaComparison.ScanRepositoryScriptPaths(root);

            Assert.That(scan.IsComplete, Is.True);
            Assert.That(scan.Paths, Is.EquivalentTo(new[] { "dbo/Tables/Users.sql" }));
        }

        [Test]
        public void ScanRepositoryScriptPaths_ReportsIncomplete_WhenTheDirectoryCannotBeRead()
        {
            // 브랜치의 내용을 하나도 읽지 못했는데 빈 목록만 돌려주면, 화면은 그것을
            // "브랜치와 일치합니다"로 옮긴다 - 이 기능이 막으려는 바로 그 문장이다.
            var missing = Path.Combine(Path.GetTempPath(), "dbvc_absent_" + Guid.NewGuid().ToString("N"));

            var scan = SchemaComparison.ScanRepositoryScriptPaths(missing);

            Assert.That(scan.Paths, Is.Empty);
            Assert.That(scan.IsComplete, Is.False);
        }

        [Test]
        public void ComparisonResult_IsInSync_WhenThereAreNoDifferences()
        {
            var result = new ComparisonResult { ComparedCount = 12 };

            Assert.That(result.IsInSync, Is.True);

            result.Differences.Add(new SchemaDifference("dbo.Users", "dbo/Tables/Users.sql", "Table", ObjectDiffState.Modified));

            Assert.That(result.IsInSync, Is.False);
        }
    }
}
