using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class ConfigManagerTests
    {
        [Test]
        public void GetMapping_ReturnsDefaultPath_WhenNoMappingAdded()
        {
            var manager = new ConfigManager();
            var path = manager.GetMapping("LocalServer", "SalesDB");
            
            Assert.That(path, Is.Not.Null);
            Assert.That(path, Is.EqualTo(@"C:\Git\LocalServer\SalesDB"));
        }

        [Test]
        public void GetMapping_ReturnsConfiguredPath_WhenMappingAdded()
        {
            var manager = new ConfigManager();
            var expectedPath = @"D:\Repositories\SalesRepo";
            
            manager.AddMapping(new MappingConfig
            {
                ServerName = "LocalServer",
                DatabaseName = "SalesDB",
                GitPath = expectedPath
            });

            var path = manager.GetMapping("LocalServer", "SalesDB");

            Assert.That(path, Is.EqualTo(expectedPath));
        }

        [Test]
        public void GetMapping_IsCaseInsensitiveForServerAndDatabaseName()
        {
            var manager = new ConfigManager();
            var expectedPath = @"D:\Repositories\SalesRepo";

            manager.AddMapping(new MappingConfig
            {
                ServerName = "LocalServer",
                DatabaseName = "SalesDB",
                GitPath = expectedPath
            });

            var path = manager.GetMapping("localserver", "salesdb");

            Assert.That(path, Is.EqualTo(expectedPath));
        }
    }
}
