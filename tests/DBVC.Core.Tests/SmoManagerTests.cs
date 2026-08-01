using System;
using System.Collections.Generic;
using System.IO;
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


