using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SmoManagerTests
    {
        [Test]
        public void ScriptObjects_GivenValidDb_GeneratesFileOrHandlesUnreachableDb()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "dbvc_test_" + Path.GetRandomFileName());
            try
            {
                var config = new ConfigManager();
                config.AddMapping(new MappingConfig
                {
                    ServerName = "localhost",
                    DatabaseName = "master",
                    GitPath = tempDir
                });
                var smo = new SmoManager(config);

                bool result = smo.ScriptObjects("localhost", "master");

                if (result)
                {
                    Assert.That(Directory.Exists(tempDir), Is.True, "Output directory should be created when scripting succeeds");
                    var sqlFiles = Directory.GetFiles(tempDir, "*.sql", SearchOption.AllDirectories);
                    Assert.That(sqlFiles, Is.Not.Empty, "Expected .sql files to be generated when scripting succeeds");
                }
                else
                {
                    Assert.That(result, Is.False, "Expected ScriptObjects to return false when database server connection fails");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        [Test]
        public void ScriptObjects_WithInvalidServerOrDb_ReturnsFalse()
        {
            var smo = new SmoManager();
            bool result = smo.ScriptObjects("invalid_server_xyz", "invalid_db_xyz");
            Assert.That(result, Is.False);
        }

        [Test]
        [TestCase(null, "master")]
        [TestCase("", "master")]
        [TestCase("   ", "master")]
        [TestCase("localhost", null)]
        [TestCase("localhost", "")]
        [TestCase("localhost", "   ")]
        public void ScriptObjects_WithNullOrWhitespaceServerOrDb_ReturnsFalse(string? serverName, string? databaseName)
        {
            var smo = new SmoManager();
            bool result = smo.ScriptObjects(serverName!, databaseName!);
            Assert.That(result, Is.False);
        }

        [Test]
        public void SmoManager_Constructor_DefaultConfigManager_Instantiates()
        {
            var smo = new SmoManager();
            Assert.That(smo, Is.Not.Null);
        }

        // ---------- ScriptAll: 설계 3.1의 부분 실패 허용 ----------

        private static ScriptTargetInfo Target(string schema, string type, string name)
            => new ScriptTargetInfo { Schema = schema, ObjectType = type, Name = name };

        [Test]
        public void ScriptAll_WritesOneFilePerObjectUsingTheSchemaTypeConvention()
        {
            var root = NewTempDir();
            try
            {
                var targets = new[]
                {
                    Target("dbo", "Table", "Users"),
                    Target("sales", "StoredProcedure", "usp_GetOrders")
                };

                var result = SmoManager.ScriptAll(targets, root, (t, outputPath) => File.WriteAllText(outputPath, $"-- {t.Name}"));

                Assert.That(result.SucceededCount, Is.EqualTo(2));
                Assert.That(result.FailedObjects, Is.Empty);
                Assert.That(File.Exists(Path.Combine(root, "dbo", "Tables", "Users.sql")), Is.True);
                Assert.That(File.Exists(Path.Combine(root, "sales", "StoredProcedures", "usp_GetOrders.sql")), Is.True);
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_ContinuesWithRemainingObjects_WhenOneObjectFails()
        {
            // 설계 3.1: "특정 객체 스크립팅 실패 시 해당 객체만 실패로 처리하고
            //           전체 스크립팅 프로세스가 중단되지 않도록"
            var root = NewTempDir();
            try
            {
                var targets = new[]
                {
                    Target("dbo", "Table", "Good1"),
                    Target("dbo", "Table", "Bad"),
                    Target("dbo", "Table", "Good2")
                };

                var result = SmoManager.ScriptAll(targets, root, (t, outputPath) =>
                {
                    if (t.Name == "Bad") throw new InvalidOperationException("scripting blew up");
                    File.WriteAllText(outputPath, $"-- {t.Name}");
                });

                Assert.That(result.SucceededCount, Is.EqualTo(2), "실패한 객체 이후의 객체도 계속 처리되어야 합니다");
                Assert.That(File.Exists(Path.Combine(root, "dbo", "Tables", "Good2.sql")), Is.True);
                Assert.That(result.FailedObjects, Is.EqualTo(new[] { "dbo.Bad" }));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_ReportsFailure_WhenEveryObjectFails()
        {
            var root = NewTempDir();
            try
            {
                var targets = new[] { Target("dbo", "Table", "Bad") };

                var result = SmoManager.ScriptAll(targets, root, (t, outputPath) => throw new InvalidOperationException("nope"));

                Assert.That(result.SucceededCount, Is.EqualTo(0));
                Assert.That(result.FailedObjects.Count, Is.EqualTo(1));
            }
            finally { TryDelete(root); }
        }

        // ---------- ScriptAll: 내용이 같으면 파일을 건드리지 않는다 ----------
        //
        // libgit2의 status는 인덱스에 기록된 stat 정보(크기·mtime)가 작업 트리와 일치하면
        // 파일 내용을 읽지 않는다. 내용이 같은데도 매번 덮어쓰면 그 캐시가 전부 무효화되어
        // status가 추적 파일 전부를 다시 해시하게 된다 — 객체 3000개 기준 18ms가 6.6초가 된다.

        [Test]
        public void ScriptAll_DoesNotTouchFile_WhenGeneratedContentIsIdentical()
        {
            var root = NewTempDir();
            try
            {
                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.WriteAllText(finalPath, "CREATE TABLE [dbo].[Users]");

                var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(finalPath, stamp);

                var result = SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => File.WriteAllText(outputPath, "CREATE TABLE [dbo].[Users]"));

                Assert.That(File.GetLastWriteTimeUtc(finalPath), Is.EqualTo(stamp),
                    "내용이 같으면 파일을 다시 쓰지 않아야 git 인덱스의 stat 캐시가 유지된다");
                Assert.That(result.SucceededCount, Is.EqualTo(1),
                    "쓰지 않았더라도 추출 자체는 성공으로 집계되어야 한다");
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_RewritesFile_WhenGeneratedContentDiffers()
        {
            var root = NewTempDir();
            try
            {
                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.WriteAllText(finalPath, "CREATE TABLE [dbo].[Users] (Id int)");

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => File.WriteAllText(outputPath, "CREATE TABLE [dbo].[Users] (Id bigint)"));

                Assert.That(File.ReadAllText(finalPath), Is.EqualTo("CREATE TABLE [dbo].[Users] (Id bigint)"));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_PreservesBytesExactly_WhenContentDiffers()
        {
            // 인코딩이 바뀌면 내용이 같은 객체도 전부 "변경됨"으로 보인다.
            // SMO가 쓴 바이트를 그대로 옮겨야 업그레이드 직후 가짜 변경이 생기지 않는다.
            var root = NewTempDir();
            try
            {
                var expected = new byte[] { 0xFF, 0xFE, 0x43, 0x00, 0x52, 0x00 }; // UTF-16LE BOM + "CR"

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => File.WriteAllBytes(outputPath, expected));

                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(expected));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_LeavesNoExtraFilesInRepository()
        {
            // 임시 파일이 작업 트리에 남으면 git이 미추적 파일로 잡아 목록을 오염시킨다.
            var root = NewTempDir();
            try
            {
                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users"), Target("dbo", "View", "vUsers") },
                    root,
                    (t, outputPath) => File.WriteAllText(outputPath, $"-- {t.Name}"));

                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(p => p.Substring(root.Length + 1).Replace('\\', '/'))
                    .OrderBy(p => p)
                    .ToArray();

                Assert.That(files, Is.EqualTo(new[] { "dbo/Tables/Users.sql", "dbo/Views/vUsers.sql" }));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_LeavesNoExtraFilesInRepository_WhenScriptingFails()
        {
            var root = NewTempDir();
            try
            {
                var result = SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Bad") },
                    root,
                    (t, outputPath) =>
                    {
                        File.WriteAllText(outputPath, "부분적으로 쓰다가");
                        throw new InvalidOperationException("scripting blew up");
                    });

                Assert.That(result.FailedObjects, Is.EqualTo(new[] { "dbo.Bad" }));
                Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories), Is.Empty,
                    "실패한 객체의 반쯤 쓰인 결과물이 작업 트리에 남으면 안 된다");
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ScriptAll_KeepsExistingFileIntact_WhenScriptingFails()
        {
            var root = NewTempDir();
            try
            {
                var finalPath = Path.Combine(root, "dbo", "Tables", "Users.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.WriteAllText(finalPath, "이전에 성공한 추출 결과");

                SmoManager.ScriptAll(
                    new[] { Target("dbo", "Table", "Users") },
                    root,
                    (t, outputPath) => throw new InvalidOperationException("nope"));

                Assert.That(File.ReadAllText(finalPath), Is.EqualTo("이전에 성공한 추출 결과"),
                    "추출에 실패했다고 직전에 성공한 파일을 잃어서는 안 된다");
            }
            finally { TryDelete(root); }
        }

        // ---------- 객체 필터 ----------

        [Test]
        public void ShouldInclude_IncludesEverything_WhenNoFilterGiven()
        {
            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), null), Is.True);
        }

        [Test]
        public void ShouldInclude_MatchesSchemaQualifiedName()
        {
            var filter = SmoManager.BuildFilter(new List<string> { "dbo.Users" });

            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), filter), Is.True);
            Assert.That(SmoManager.ShouldInclude(Target("app", "Table", "Users"), filter), Is.False,
                "스키마가 다른 동명 객체를 구분해야 합니다");
        }

        [Test]
        public void ShouldInclude_MatchesUnqualifiedNameForConvenience()
        {
            var filter = SmoManager.BuildFilter(new List<string> { "Users" });

            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), filter), Is.True);
            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Orders"), filter), Is.False);
        }

        [Test]
        public void ShouldInclude_IsCaseInsensitive()
        {
            var filter = SmoManager.BuildFilter(new List<string> { "DBO.USERS" });
            Assert.That(SmoManager.ShouldInclude(Target("dbo", "Table", "Users"), filter), Is.True);
        }

        private static string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_smo_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, true); } catch { }
            }
        }
    }
}


