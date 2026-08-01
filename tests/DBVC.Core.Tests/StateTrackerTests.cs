using System.Linq;
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

        [Test]
        public void IsInitialized_ReturnsFalse_WhenNoTable()
        {
            var tracker = new StateTracker();
            Assert.That(tracker.IsInitialized("fake_connection_string"), Is.False);
        }

        [Test]
        public void InitializeDatabase_ThrowsArgumentException_WhenConnectionStringIsEmpty()
        {
            var tracker = new StateTracker();
            Assert.Throws<System.ArgumentException>(() => tracker.InitializeDatabase(""));
        }

        [Test]
        public void InstallScript_IsEmbeddedAndSplitsIntoMultipleBatches()
        {
            var batches = StateTracker.SplitSqlBatches(StateTracker.ReadInstallScript());

            Assert.That(batches.Count, Is.GreaterThan(1), "설치 스크립트는 GO 기준으로 여러 배치로 나뉘어야 합니다");
            Assert.That(batches, Has.All.Matches<string>(b => !string.IsNullOrWhiteSpace(b)));
        }

        [Test]
        public void InstallScript_PutsCreateTriggerFirstInItsBatch()
        {
            // SQL Server는 CREATE TRIGGER가 배치의 첫 구문일 것을 요구한다.
            // 이 규칙이 깨지면 설치가 런타임에 실패한다.
            var batches = StateTracker.SplitSqlBatches(StateTracker.ReadInstallScript());

            var triggerBatches = batches
                .Where(b => b.IndexOf("CREATE TRIGGER", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            Assert.That(triggerBatches, Is.Not.Empty, "CREATE TRIGGER 배치가 있어야 합니다");
            foreach (var batch in triggerBatches)
            {
                Assert.That(batch.TrimStart(), Does.StartWith("CREATE TRIGGER").IgnoreCase,
                    "CREATE TRIGGER는 배치의 첫 구문이어야 합니다");
            }
        }

        [Test]
        public void InstallScript_CreatesChangeLogWithSyncAndSchemaColumns()
        {
            var script = StateTracker.ReadInstallScript();

            Assert.That(script, Does.Contain("IsProcessed"),
                "커밋된 변경을 걸러내려면 동기화 워터마크 컬럼이 필요합니다");
            Assert.That(script, Does.Contain("SchemaName"),
                "[Schema]/[ObjectType]/[Name].sql 경로를 유도하려면 스키마명이 필요합니다");
        }

        [Test]
        public void InstallScript_IsIdempotentForExistingInstallations()
        {
            var script = StateTracker.ReadInstallScript();

            // 이미 설치된 DB에도 새 컬럼이 추가되도록 ALTER 경로가 있어야 한다.
            Assert.That(script, Does.Contain("ALTER TABLE").IgnoreCase);
            Assert.That(script, Does.Contain("sys.columns").IgnoreCase);
        }

        [Test]
        public void InitializeDatabase_LoadsEmbeddedScriptAndAttemptsConnection_WhenConnectionStringIsProvided()
        {
            var tracker = new StateTracker();
            var ex = Assert.Catch<System.Exception>(() => tracker.InitializeDatabase("Server=dummy;Database=dummy;Integrated Security=True;TrustServerCertificate=True;"));
            Assert.That(ex, Is.Not.InstanceOf<System.IO.FileNotFoundException>());
        }
    }
}
