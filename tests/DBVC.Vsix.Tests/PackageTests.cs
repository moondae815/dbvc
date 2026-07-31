using NUnit.Framework;
using DBVC.Vsix;
using DBVC.Core;

namespace DBVC.Vsix.Tests
{
    [TestFixture]
    public class PackageTests
    {
        [Test]
        public void Package_CanBeInstantiated()
        {
            var pkg = new DbvcPackage();
            Assert.That(pkg, Is.Not.Null);
            Assert.That(pkg.ConfigManager, Is.Not.Null);
            Assert.That(pkg.GitManager, Is.Not.Null);
            Assert.That(pkg.SmoManager, Is.Not.Null);
            Assert.That(pkg.StateTracker, Is.Not.Null);
        }

        [Test]
        public void Package_CanBeInstantiatedWithCustomDependencies()
        {
            var config = new ConfigManager();
            var git = new GitManager(config);
            var smo = new SmoManager();
            var state = new StateTracker();

            var pkg = new DbvcPackage(config, git, smo, state);

            Assert.That(pkg.ConfigManager, Is.SameAs(config));
            Assert.That(pkg.GitManager, Is.SameAs(git));
            Assert.That(pkg.SmoManager, Is.SameAs(smo));
            Assert.That(pkg.StateTracker, Is.SameAs(state));
        }

        [Test]
        public void Package_Constructor_ThrowsArgumentNullException_WhenConfigManagerIsNull()
        {
            var config = new ConfigManager();
            var git = new GitManager(config);
            var smo = new SmoManager();
            var state = new StateTracker();

            Assert.Throws<System.ArgumentNullException>(() => new DbvcPackage(null!, git, smo, state));
        }

        [Test]
        public void Package_Constructor_ThrowsArgumentNullException_WhenGitManagerIsNull()
        {
            var config = new ConfigManager();
            var smo = new SmoManager();
            var state = new StateTracker();

            Assert.Throws<System.ArgumentNullException>(() => new DbvcPackage(config, null!, smo, state));
        }

        [Test]
        public void Package_Constructor_ThrowsArgumentNullException_WhenSmoManagerIsNull()
        {
            var config = new ConfigManager();
            var git = new GitManager(config);
            var state = new StateTracker();

            Assert.Throws<System.ArgumentNullException>(() => new DbvcPackage(config, git, null!, state));
        }

        [Test]
        public void Package_Constructor_ThrowsArgumentNullException_WhenStateTrackerIsNull()
        {
            var config = new ConfigManager();
            var git = new GitManager(config);
            var smo = new SmoManager();

            Assert.Throws<System.ArgumentNullException>(() => new DbvcPackage(config, git, smo, null!));
        }
    }
}
