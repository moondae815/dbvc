using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class StateTrackerTests
    {
        [Test]
        public void GetPendingChanges_ReturnsList()
        {
            var tracker = new StateTracker();
            var changes = tracker.GetPendingChanges("conn");
            Assert.That(changes, Is.Not.Null);
        }

        [Test]
        public void RefreshState_HandlesMissingDatabaseGracefully()
        {
            var config = new ConfigManager();
            config.AddMapping(new MappingConfig { ServerName = "localhost", DatabaseName = "nonexistent_db", GitPath = "path" });
            var tracker = new StateTracker(config);

            // Should not throw, should handle SqlException internally
            Assert.DoesNotThrow(() => tracker.RefreshState("localhost", "nonexistent_db"));
        }

        [Test]
        public void GetObjectState_ReturnsCleanByDefault()
        {
            var config = new ConfigManager();
            var tracker = new StateTracker(config);
            var state = tracker.GetObjectState("localhost", "db", "dbo.TestTable");
            Assert.That(state, Is.EqualTo("Clean"));
        }

        [Test]
        public void StateTracker_Constructor_ThrowsArgumentNullException_WhenConfigManagerIsNull()
        {
            Assert.Throws<System.ArgumentNullException>(() => new StateTracker(null!));
        }
    }
}
