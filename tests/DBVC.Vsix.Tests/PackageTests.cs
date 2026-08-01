using System;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Vsix;

namespace DBVC.Vsix.Tests
{
    [TestFixture]
    public class DbvcServicesTests
    {
        private static ConfigManager NewIsolatedConfig()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dbvc_cfg_" + Guid.NewGuid().ToString("N"),
                "mappings.json");
            return new ConfigManager(path);
        }

        [Test]
        public void Services_CanBeConstructedWithDefaults()
        {
            var services = new DbvcServices();

            Assert.That(services.ConfigManager, Is.Not.Null);
            Assert.That(services.GitManager, Is.Not.Null);
            Assert.That(services.SmoManager, Is.Not.Null);
            Assert.That(services.StateTracker, Is.Not.Null);
        }

        [Test]
        public void Services_ShareTheSameConfigManagerInstance()
        {
            // 매니저들이 서로 다른 ConfigManager를 들면 한쪽에서 추가한 매핑을
            // 다른 쪽이 보지 못한다.
            var config = NewIsolatedConfig();
            var services = new DbvcServices(config);

            config.AddMapping("S", "DB", @"C:\repo");

            Assert.That(services.ConfigManager.GetMapping("S", "DB"), Is.EqualTo(@"C:\repo"));
            Assert.That(services.GitManager.GetStatusForDatabase("S", "DB"), Is.EqualTo("Unknown"),
                "매핑은 보이지만 해당 경로가 저장소가 아니므로 Unknown이어야 합니다");
        }

        [Test]
        public void Services_CanBeConstructedWithCustomDependencies()
        {
            var config = NewIsolatedConfig();
            var git = new GitManager(config);
            var smo = new SmoManager(config);
            var state = new StateTracker(config, git);

            var services = new DbvcServices(config, git, smo, state);

            Assert.That(services.ConfigManager, Is.SameAs(config));
            Assert.That(services.GitManager, Is.SameAs(git));
            Assert.That(services.SmoManager, Is.SameAs(smo));
            Assert.That(services.StateTracker, Is.SameAs(state));
        }

        [Test]
        public void Services_Constructor_ThrowsArgumentNullException_ForEachMissingDependency()
        {
            var config = NewIsolatedConfig();
            var git = new GitManager(config);
            var smo = new SmoManager(config);
            var state = new StateTracker(config, git);

            Assert.Throws<ArgumentNullException>(() => new DbvcServices(null!, git, smo, state));
            Assert.Throws<ArgumentNullException>(() => new DbvcServices(config, null!, smo, state));
            Assert.Throws<ArgumentNullException>(() => new DbvcServices(config, git, null!, state));
            Assert.Throws<ArgumentNullException>(() => new DbvcServices(config, git, smo, null!));
        }

        [Test]
        public void Services_CreateViewChangesViewModel_WiresUpTheSharedManagers()
        {
            var config = NewIsolatedConfig();
            var services = new DbvcServices(config);

            var vm = services.CreateViewChangesViewModel();

            Assert.That(vm, Is.Not.Null);
            Assert.That(vm.IsInitialized, Is.False);
        }
    }
}
