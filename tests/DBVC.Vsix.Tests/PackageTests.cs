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
    }
}
