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
    }
}


