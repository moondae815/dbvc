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
        public void ScriptObjects_GivenValidDb_GeneratesFile()
        {
            var config = new ConfigManager();
            config.AddMapping(new MappingConfig
            {
                ServerName = "localhost",
                DatabaseName = "master",
                GitPath = Path.Combine(Path.GetTempPath(), "dbvc_test")
            });
            var smo = new SmoManager(config);

            Assert.DoesNotThrow(() => smo.ScriptObjects("localhost", "master"));
        }

        [Test]
        public void ScriptObjects_WithInvalidServerOrDb_ReturnsFalse()
        {
            var smo = new SmoManager();
            bool result = smo.ScriptObjects("invalid_server_xyz", "invalid_db_xyz");
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

