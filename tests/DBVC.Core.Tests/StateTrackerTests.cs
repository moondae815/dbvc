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

        [Test]
        public void ProcessChangeLogRows_PreservesNewestEvent_WhenMultipleEventsForSameObjectExist()
        {
            var tracker = new StateTracker();
            // Order BY EventDate DESC -> Newest event first (ALTER_TABLE), older event second (CREATE_TABLE)
            var rows = new[]
            {
                ("dbo.TestTable", "ALTER_TABLE"),
                ("dbo.TestTable", "CREATE_TABLE")
            };

            tracker.ProcessChangeLogRows("LocalServer", "TestDB", rows);

            var state = tracker.GetObjectState("LocalServer", "TestDB", "dbo.TestTable");
            Assert.That(state, Is.EqualTo("ALTER_TABLE"), "Should preserve the newest event (first encountered in DESC order)");
        }

        [Test]
        public void GetObjectState_IsCaseInsensitiveForServerDatabaseAndObjectName()
        {
            var tracker = new StateTracker();
            var rows = new[]
            {
                ("dbo.Customers", "CREATE_TABLE")
            };

            tracker.ProcessChangeLogRows("LocalServer", "SalesDB", rows);

            var stateLower = tracker.GetObjectState("localserver", "salesdb", "dbo.customers");
            var stateUpper = tracker.GetObjectState("LOCALSERVER", "SALESDB", "DBO.CUSTOMERS");

            Assert.That(stateLower, Is.EqualTo("CREATE_TABLE"));
            Assert.That(stateUpper, Is.EqualTo("CREATE_TABLE"));
        }

        [Test]
        public void ProcessChangeLogRows_PopulatesStateCacheForMultipleObjects()
        {
            var tracker = new StateTracker();
            var rows = new[]
            {
                ("dbo.Orders", "ALTER_TABLE"),
                ("dbo.Customers", "CREATE_TABLE")
            };

            tracker.ProcessChangeLogRows("Server1", "DB1", rows);

            Assert.That(tracker.GetObjectState("Server1", "DB1", "dbo.Orders"), Is.EqualTo("ALTER_TABLE"));
            Assert.That(tracker.GetObjectState("Server1", "DB1", "dbo.Customers"), Is.EqualTo("CREATE_TABLE"));
            Assert.That(tracker.GetObjectState("Server1", "DB1", "dbo.Products"), Is.EqualTo("Clean"));
        }
    }
}
