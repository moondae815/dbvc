using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class ConfigManagerTests
    {
        private static ConfigManager NewIsolatedManager(out string configPath)
        {
            configPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dbvc_cfg_" + System.Guid.NewGuid().ToString("N"),
                "mappings.json");
            return new ConfigManager(configPath);
        }

        private static ConfigManager NewIsolatedManager()
        {
            return NewIsolatedManager(out _);
        }

        [Test]
        public void GetMapping_ReturnsNull_WhenNoMappingAdded()
        {
            var manager = NewIsolatedManager();
            var path = manager.GetMapping("LocalServer", "SalesDB");

            Assert.That(path, Is.Null, "매핑이 없으면 가짜 경로가 아니라 null을 반환해야 합니다");
        }

        [Test]
        public void GetMapping_ReturnsNull_WhenGitPathIsEmpty()
        {
            var manager = NewIsolatedManager();
            manager.AddMapping(new MappingConfig
            {
                ServerName = "LocalServer",
                DatabaseName = "SalesDB",
                GitPath = "   "
            });

            Assert.That(manager.GetMapping("LocalServer", "SalesDB"), Is.Null);
        }

        [Test]
        public void TryGetMapping_ReturnsNull_WhenNoMappingAdded()
        {
            var manager = NewIsolatedManager();
            Assert.That(manager.TryGetMapping("LocalServer", "SalesDB"), Is.Null);
        }

        [Test]
        public void TryGetMapping_ReturnsMapping_WhenMappingAdded()
        {
            var manager = NewIsolatedManager();
            manager.AddMapping("LocalServer", "SalesDB", @"D:\Repositories\SalesRepo");

            var mapping = manager.TryGetMapping("localserver", "salesdb");

            Assert.That(mapping, Is.Not.Null);
            Assert.That(mapping!.GitPath, Is.EqualTo(@"D:\Repositories\SalesRepo"));
        }

        [Test]
        public void RemoveMapping_RemovesTheEntry()
        {
            var manager = NewIsolatedManager();
            manager.AddMapping("LocalServer", "SalesDB", @"D:\Repositories\SalesRepo");

            var removed = manager.RemoveMapping("LocalServer", "SalesDB");

            Assert.That(removed, Is.True);
            Assert.That(manager.GetMapping("LocalServer", "SalesDB"), Is.Null);
        }

        [Test]
        public void GetAllMappings_ReturnsEveryConfiguredMapping()
        {
            var manager = NewIsolatedManager();
            manager.AddMapping("S1", "DB1", @"C:\r1");
            manager.AddMapping("S2", "DB2", @"C:\r2");

            var all = manager.GetAllMappings();

            Assert.That(all.Count, Is.EqualTo(2));
        }

        [Test]
        public void AddMapping_PersistsToDisk_AndIsRestoredByANewInstance()
        {
            var manager = NewIsolatedManager(out var configPath);
            try
            {
                manager.AddMapping("LocalServer", "SalesDB", @"D:\Repositories\SalesRepo");

                Assert.That(System.IO.File.Exists(configPath), Is.True, "AddMapping은 설정 파일을 저장해야 합니다");

                var reloaded = new ConfigManager(configPath);
                Assert.That(reloaded.GetMapping("LocalServer", "SalesDB"), Is.EqualTo(@"D:\Repositories\SalesRepo"));
            }
            finally
            {
                CleanUp(configPath);
            }
        }

        [Test]
        public void RemoveMapping_PersistsRemovalToDisk()
        {
            var manager = NewIsolatedManager(out var configPath);
            try
            {
                manager.AddMapping("LocalServer", "SalesDB", @"D:\Repositories\SalesRepo");
                manager.RemoveMapping("LocalServer", "SalesDB");

                var reloaded = new ConfigManager(configPath);
                Assert.That(reloaded.GetMapping("LocalServer", "SalesDB"), Is.Null);
            }
            finally
            {
                CleanUp(configPath);
            }
        }

        [Test]
        public void Constructor_IgnoresCorruptConfigFile()
        {
            var configPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dbvc_cfg_" + System.Guid.NewGuid().ToString("N"),
                "mappings.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(configPath)!);
            System.IO.File.WriteAllText(configPath, "{ this is not json");

            try
            {
                ConfigManager manager = null!;
                Assert.DoesNotThrow(() => manager = new ConfigManager(configPath));
                Assert.That(manager.GetAllMappings(), Is.Empty);
            }
            finally
            {
                CleanUp(configPath);
            }
        }

        [Test]
        public void DefaultConfigFilePath_IsUnderApplicationDataDbvcFolder()
        {
            var path = ConfigManager.DefaultConfigFilePath;

            Assert.That(System.IO.Path.GetFileName(path), Is.EqualTo("mappings.json"));
            Assert.That(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)), Is.EqualTo("DBVC"));
        }

        private static void CleanUp(string configPath)
        {
            var dir = System.IO.Path.GetDirectoryName(configPath);
            if (dir != null && System.IO.Directory.Exists(dir))
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void GetMapping_ReturnsConfiguredPath_WhenMappingAdded()
        {
            var manager = NewIsolatedManager();
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
            var manager = NewIsolatedManager();
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

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void GetMapping_ThrowsArgumentException_WhenServerNameInvalid(string? invalidServerName)
        {
            var manager = NewIsolatedManager();
            Assert.Throws<System.ArgumentException>(() => manager.GetMapping(invalidServerName!, "SalesDB"));
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void GetMapping_ThrowsArgumentException_WhenDatabaseNameInvalid(string? invalidDatabaseName)
        {
            var manager = NewIsolatedManager();
            Assert.Throws<System.ArgumentException>(() => manager.GetMapping("LocalServer", invalidDatabaseName!));
        }

        [Test]
        public void AddMapping_ThrowsArgumentNullException_WhenMappingIsNull()
        {
            var manager = NewIsolatedManager();
            Assert.Throws<System.ArgumentNullException>(() => manager.AddMapping(null!));
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void AddMapping_ThrowsArgumentException_WhenServerNameInvalid(string? invalidServerName)
        {
            var manager = NewIsolatedManager();
            var config = new MappingConfig { ServerName = invalidServerName!, DatabaseName = "SalesDB", GitPath = "path" };
            Assert.Throws<System.ArgumentException>(() => manager.AddMapping(config));
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void AddMapping_ThrowsArgumentException_WhenDatabaseNameInvalid(string? invalidDatabaseName)
        {
            var manager = NewIsolatedManager();
            var config = new MappingConfig { ServerName = "LocalServer", DatabaseName = invalidDatabaseName!, GitPath = "path" };
            Assert.Throws<System.ArgumentException>(() => manager.AddMapping(config));
        }
    }
}
