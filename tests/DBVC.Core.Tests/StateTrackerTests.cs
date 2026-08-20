using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class StateTrackerTests
    {
        private static ConfigManager NewIsolatedConfig()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dbvc_cfg_" + System.Guid.NewGuid().ToString("N"),
                "mappings.json");
            return new ConfigManager(path);
        }

        /// <summary>
        /// 각 테스트가 독립된 인증 저장소를 쓰게 한다. 메모리 전용이라 격리를 위해 디스크 경로를
        /// 나눌 필요는 없지만, 인스턴스를 공유하면 한 테스트가 넣은 인증 정보가 다른 테스트에서도 보인다.
        /// </summary>
        private static SessionCredentialStore NewIsolatedCredentialStore() => new SessionCredentialStore();

        private static StateTracker NewTracker()
            => new StateTracker(NewIsolatedConfig(), null, NewIsolatedCredentialStore());

        private static ChangeLogRow Row(long id, string schema, string name, string objectType, string eventType)
            => new ChangeLogRow { Id = id, SchemaName = schema, ObjectName = name, ObjectType = objectType, EventType = eventType };

        // ---------- 생성자 ----------

        [Test]
        public void StateTracker_Constructor_ThrowsArgumentNullException_WhenConfigManagerIsNull()
        {
            Assert.Throws<System.ArgumentNullException>(() => new StateTracker(null!));
        }

        // ---------- EventType -> 상태 매핑 ----------

        [Test]
        [TestCase("CREATE_TABLE", "Added")]
        [TestCase("CREATE_PROCEDURE", "Added")]
        [TestCase("ALTER_TABLE", "Modified")]
        [TestCase("ALTER_PROCEDURE", "Modified")]
        [TestCase("RENAME", "Modified")]
        [TestCase("DROP_TABLE", "Deleted")]
        [TestCase("DROP_VIEW", "Deleted")]
        public void MapEventTypeToState_TranslatesDdlEventsToUiStates(string eventType, string expected)
        {
            // ChangeItemViewModel.State의 계약은 Modified/Added/Deleted이며
            // DDL 로그의 원시 EventType을 그대로 노출해서는 안 된다.
            Assert.That(StateTracker.MapEventTypeToState(eventType), Is.EqualTo(expected));
        }

        [Test]
        public void MapEventTypeToState_DefaultsToModified_ForUnrecognizedEvents()
        {
            Assert.That(StateTracker.MapEventTypeToState("SOMETHING_ELSE"), Is.EqualTo("Modified"));
        }

        // ---------- 변경 집합 구성 ----------

        [Test]
        public void BuildChangeSet_KeepsNewestEventPerObject()
        {
            var tracker = NewTracker();
            // 조회는 PostTime DESC이므로 최신 이벤트가 먼저 온다.
            var rows = new[]
            {
                Row(20, "dbo", "TestTable", "TABLE", "ALTER_TABLE"),
                Row(10, "dbo", "TestTable", "TABLE", "CREATE_TABLE")
            };

            var changes = tracker.BuildChangeSet(rows, null);

            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].State, Is.EqualTo("Modified"));
            Assert.That(changes[0].LastLogId, Is.EqualTo(20));
        }

        [Test]
        public void BuildChangeSet_DerivesRepositoryRelativePathFromSchemaAndObjectType()
        {
            var tracker = NewTracker();

            var changes = tracker.BuildChangeSet(new[] { Row(1, "sales", "usp_GetOrders", "PROCEDURE", "ALTER_PROCEDURE") }, null);

            Assert.That(changes[0].RelativePath, Is.EqualTo("sales/StoredProcedures/usp_GetOrders.sql"));
            Assert.That(changes[0].QualifiedName, Is.EqualTo("sales.usp_GetOrders"));
        }

        [Test]
        public void BuildChangeSet_IncludesFilesThatAreDirtyInGitButAbsentFromTheDdlLog()
        {
            // 설계 3.3: Git 상태와 DB 로그를 "종합"한다.
            // 트리거 설치 이전에 만들어진 객체는 로그에 없지만 Git에는 변경으로 남는다.
            var tracker = NewTracker();
            var gitStates = new Dictionary<string, string>
            {
                ["dbo/Views/vw_Legacy.sql"] = "Added"
            };

            var changes = tracker.BuildChangeSet(System.Array.Empty<ChangeLogRow>(), gitStates);

            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].QualifiedName, Is.EqualTo("dbo.vw_Legacy"));
            Assert.That(changes[0].State, Is.EqualTo("Added"));
            Assert.That(changes[0].RelativePath, Is.EqualTo("dbo/Views/vw_Legacy.sql"));
        }

        [Test]
        public void BuildChangeSet_PrefersDdlLogState_WhenObjectAppearsInBothSources()
        {
            var tracker = NewTracker();
            var gitStates = new Dictionary<string, string>
            {
                ["dbo/Tables/Users.sql"] = "Modified"
            };

            var changes = tracker.BuildChangeSet(new[] { Row(5, "dbo", "Users", "TABLE", "DROP_TABLE") }, gitStates);

            Assert.That(changes.Count, Is.EqualTo(1), "같은 객체가 두 소스에 있어도 중복되면 안 됩니다");
            Assert.That(changes[0].State, Is.EqualTo("Deleted"), "DB의 DDL 로그가 최종 상태의 근거입니다");
        }

        [Test]
        public void BuildChangeSet_ReportsAdded_WhenGitSeesANewFile_EvenIfNewestEventIsAlter()
        {
            // SSMS 테이블 디자이너는 저장 한 번에 CREATE_TABLE 뒤로 ALTER_TABLE을 더 흘린다.
            // 최신 이벤트만 보면 새 테이블이 "수정"으로 뜬다 — 저장소에는 방금 생긴 파일인데도.
            var tracker = NewTracker();
            var rows = new[]
            {
                Row(20, "dbo", "Table_1", "TABLE", "ALTER_TABLE"),
                Row(10, "dbo", "Table_1", "TABLE", "CREATE_TABLE")
            };
            var gitStates = new Dictionary<string, string>
            {
                ["dbo/Tables/Table_1.sql"] = "Added"
            };

            var changes = tracker.BuildChangeSet(rows, gitStates);

            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].State, Is.EqualTo("Added"));
            Assert.That(changes[0].LastLogId, Is.EqualTo(20), "MarkProcessed가 닫아야 할 행은 여전히 최신 행입니다");
        }

        [Test]
        public void BuildChangeSet_ReportsModified_WhenGitSeesATrackedFile_EvenIfNewestEventIsCreate()
        {
            // 이미 커밋된 객체를 DROP 후 다시 CREATE하면 이벤트는 CREATE지만
            // 저장소 기준으로는 기존 파일이 바뀐 것이다.
            var tracker = NewTracker();
            var gitStates = new Dictionary<string, string>
            {
                ["dbo/Tables/Users.sql"] = "Modified"
            };

            var changes = tracker.BuildChangeSet(new[] { Row(7, "dbo", "Users", "TABLE", "CREATE_TABLE") }, gitStates);

            Assert.That(changes[0].State, Is.EqualTo("Modified"));
        }

        [Test]
        public void BuildChangeSet_FallsBackToEventType_WhenGitHasNoStateForTheFile()
        {
            // 스크립트가 저장소의 것과 똑같아 Git이 아무것도 보고하지 않는 경우다.
            // 근거가 DDL 로그밖에 없으므로 이벤트 타입을 그대로 쓴다.
            var tracker = NewTracker();

            var changes = tracker.BuildChangeSet(
                new[] { Row(3, "dbo", "Products", "TABLE", "CREATE_TABLE") },
                new Dictionary<string, string>());

            Assert.That(changes[0].State, Is.EqualTo("Added"));
        }

        [Test]
        public void BuildChangeSet_KeepsDeleted_WhenNewestEventIsDrop_EvenIfGitStillSeesTheFile()
        {
            // DROP된 객체의 파일 정리는 이 판정 뒤에 일어난다. 그래서 Git은 아직 삭제를 모르거나
            // 추출이 남긴 흔적을 다른 상태로 보고한다 — 여기서는 DDL 로그만 믿어야 한다.
            var tracker = NewTracker();
            var gitStates = new Dictionary<string, string>
            {
                ["dbo/Tables/Legacy.sql"] = "Added"
            };

            var changes = tracker.BuildChangeSet(new[] { Row(9, "dbo", "Legacy", "TABLE", "DROP_TABLE") }, gitStates);

            Assert.That(changes[0].State, Is.EqualTo("Deleted"));
        }

        [Test]
        public void BuildChangeSet_ReturnsEmpty_WhenNeitherSourceHasChanges()
        {
            var tracker = NewTracker();
            Assert.That(tracker.BuildChangeSet(System.Array.Empty<ChangeLogRow>(), null), Is.Empty);
        }

        // ---------- 캐시 ----------

        [Test]
        public void ApplyChangeSet_MakesStatesVisibleThroughGetObjectState()
        {
            var tracker = NewTracker();
            var changes = tracker.BuildChangeSet(new[]
            {
                Row(1, "dbo", "Orders", "TABLE", "ALTER_TABLE"),
                Row(2, "dbo", "Customers", "TABLE", "CREATE_TABLE")
            }, null);

            tracker.ApplyChangeSet("Server1", "DB1", changes);

            Assert.That(tracker.GetObjectState("Server1", "DB1", "dbo.Orders"), Is.EqualTo("Modified"));
            Assert.That(tracker.GetObjectState("Server1", "DB1", "dbo.Customers"), Is.EqualTo("Added"));
            Assert.That(tracker.GetObjectState("Server1", "DB1", "dbo.Products"), Is.EqualTo("Clean"));
        }

        [Test]
        public void ApplyChangeSet_DropsStaleEntriesFromThePreviousRefresh()
        {
            // 커밋 후 새로고침하면 더 이상 변경이 아닌 객체는 사라져야 한다.
            var tracker = NewTracker();
            tracker.ApplyChangeSet("S", "DB", tracker.BuildChangeSet(new[] { Row(1, "dbo", "Old", "TABLE", "ALTER_TABLE") }, null));
            Assert.That(tracker.GetObjectState("S", "DB", "dbo.Old"), Is.EqualTo("Modified"));

            tracker.ApplyChangeSet("S", "DB", tracker.BuildChangeSet(new[] { Row(2, "dbo", "New", "TABLE", "ALTER_TABLE") }, null));

            Assert.That(tracker.GetObjectState("S", "DB", "dbo.Old"), Is.EqualTo("Clean"),
                "이전 새로고침의 잔여 상태가 남아 있으면 안 됩니다");
            Assert.That(tracker.GetObjectState("S", "DB", "dbo.New"), Is.EqualTo("Modified"));
        }

        [Test]
        public void ApplyChangeSet_DoesNotAffectOtherDatabases()
        {
            var tracker = NewTracker();
            tracker.ApplyChangeSet("S", "DB1", tracker.BuildChangeSet(new[] { Row(1, "dbo", "T1", "TABLE", "ALTER_TABLE") }, null));
            tracker.ApplyChangeSet("S", "DB2", tracker.BuildChangeSet(new[] { Row(2, "dbo", "T2", "TABLE", "ALTER_TABLE") }, null));

            Assert.That(tracker.GetObjectState("S", "DB1", "dbo.T1"), Is.EqualTo("Modified"));
            Assert.That(tracker.GetObjectState("S", "DB2", "dbo.T2"), Is.EqualTo("Modified"));
        }

        [Test]
        public void GetObjectState_IsCaseInsensitiveForServerDatabaseAndObjectName()
        {
            var tracker = NewTracker();
            tracker.ApplyChangeSet("LocalServer", "SalesDB",
                tracker.BuildChangeSet(new[] { Row(1, "dbo", "Customers", "TABLE", "CREATE_TABLE") }, null));

            Assert.That(tracker.GetObjectState("localserver", "salesdb", "dbo.customers"), Is.EqualTo("Added"));
            Assert.That(tracker.GetObjectState("LOCALSERVER", "SALESDB", "DBO.CUSTOMERS"), Is.EqualTo("Added"));
        }

        [Test]
        public void GetPendingChanges_ReturnsTheCachedChangeRecords()
        {
            var tracker = NewTracker();
            tracker.ApplyChangeSet("S", "DB",
                tracker.BuildChangeSet(new[] { Row(1, "dbo", "Orders", "TABLE", "ALTER_TABLE") }, null));

            var pending = tracker.GetPendingChanges("S", "DB");

            Assert.That(pending.Count, Is.EqualTo(1));
            Assert.That(pending[0].QualifiedName, Is.EqualTo("dbo.Orders"));
            Assert.That(pending[0].RelativePath, Is.EqualTo("dbo/Tables/Orders.sql"));
        }

        [Test]
        public void GetPendingChanges_ReturnsEmpty_ForUnknownDatabase()
        {
            Assert.That(NewTracker().GetPendingChanges("nope", "nope"), Is.Empty);
        }

        // ---------- RefreshState ----------

        [Test]
        public void RefreshState_ReturnsFalse_WhenDatabaseIsNotMapped()
        {
            var tracker = NewTracker();
            Assert.That(tracker.RefreshState("localhost", "unmapped_db"), Is.False);
        }

        [Test]
        public void RefreshState_HandlesUnreachableDatabaseGracefully()
        {
            var config = NewIsolatedConfig();
            config.AddMapping("localhost", "nonexistent_db", System.IO.Path.GetTempPath());
            var tracker = new StateTracker(config);

            bool result = true;
            Assert.DoesNotThrow(() => result = tracker.RefreshState("localhost", "nonexistent_db"));
            Assert.That(result, Is.False, "연결 실패는 예외 대신 false로 알려야 합니다");
        }

        // ---------- 초기화 확인 ----------

        [Test]
        public void GetInstalledVersion_ReturnsZero_WhenTheServerCannotBeReached()
        {
            Assert.That(NewTracker().GetInstalledVersion("no_such_server_hostname", "no_such_db"), Is.Zero);
        }

        [Test]
        public void GetInstalledVersion_ReturnsZero_WhenServerOrDatabaseIsMissing()
        {
            var tracker = NewTracker();
            Assert.That(tracker.GetInstalledVersion("", "db"), Is.Zero);
            Assert.That(tracker.GetInstalledVersion("server", ""), Is.Zero);
        }

        [Test]
        public void InstalledVersionQuery_ChecksTheChangeLogTableTheTriggerAndTheVersionProperty()
        {
            // 셋 중 하나라도 빠지면 구버전을 최신으로 읽거나, 설치된 것을 미설치로 읽는다.
            var query = StateTracker.InstalledVersionQuery;

            Assert.Multiple(() =>
            {
                Assert.That(query, Does.Contain("DBVC_ChangeLog"));
                Assert.That(query, Does.Contain("trg_DBVC_DDL_Tracker"));
                Assert.That(query, Does.Contain("DBVC_SchemaVersion"));
            });
        }

        [Test]
        public void RequiredSchemaVersion_IsTwo()
        {
            // 설치 스크립트가 심는 값과 같아야 한다. 어긋나면 모든 사용자에게 업데이트 배너가 계속 뜨거나
            // 구버전이 최신으로 읽힌다. 스크립트 쪽 값은 InstallScriptSyncTests가 대조한다.
            Assert.That(StateTracker.RequiredSchemaVersion, Is.EqualTo(2));
        }

        [Test]
        public void PendingChangesQuery_FiltersOutAlreadyProcessedRowsAndOrdersByPostTime()
        {
            // 설계 3.3: "아직 커밋되지 않은(또는 마지막 동기화 이후의)" 로그만 읽는다.
            var query = StateTracker.PendingChangesQuery;

            Assert.That(query, Does.Contain("IsProcessed"));
            Assert.That(query, Does.Contain("PostTime"));
            Assert.That(query, Does.Not.Contain("EventDate"),
                "DBVC_ChangeLog에는 EventDate 컬럼이 없습니다");
        }

        // ---------- 설치 스크립트 ----------

        // ---------- 변경분만 추출하기 위한 대상 목록 ----------
        //
        // 새로고침이 DB 전체를 다시 스크립팅하면 객체 수에 비례해 SMO 왕복이 쌓인다.
        // DDL 로그는 무엇이 바뀌었는지 이미 알고 있으므로, 그 목록을 추출 대상으로 쓴다.

        [Test]
        public void ToQualifiedNames_ReturnsSchemaQualifiedNames()
        {
            var names = StateTracker.ToQualifiedNames(new[]
            {
                new ChangeLogRow { Id = 2, SchemaName = "sales", ObjectName = "Orders", ObjectType = "TABLE", EventType = "ALTER_TABLE" },
                new ChangeLogRow { Id = 1, SchemaName = "dbo", ObjectName = "Users", ObjectType = "TABLE", EventType = "ALTER_TABLE" }
            });

            Assert.That(names, Is.EquivalentTo(new[] { "sales.Orders", "dbo.Users" }));
        }

        [Test]
        public void ToQualifiedNames_DefaultsToDboWhenSchemaIsMissing()
        {
            var names = StateTracker.ToQualifiedNames(new[]
            {
                new ChangeLogRow { Id = 1, SchemaName = null, ObjectName = "Users", ObjectType = "TABLE", EventType = "ALTER_TABLE" }
            });

            Assert.That(names, Is.EqualTo(new[] { "dbo.Users" }));
        }

        [Test]
        public void ToQualifiedNames_CollapsesRepeatedEventsForTheSameObject()
        {
            // 같은 객체를 열 번 고치면 로그 행도 열 개다. 추출은 한 번이면 된다.
            var names = StateTracker.ToQualifiedNames(new[]
            {
                new ChangeLogRow { Id = 3, SchemaName = "dbo", ObjectName = "Users", ObjectType = "TABLE", EventType = "ALTER_TABLE" },
                new ChangeLogRow { Id = 2, SchemaName = "dbo", ObjectName = "Users", ObjectType = "TABLE", EventType = "ALTER_TABLE" },
                new ChangeLogRow { Id = 1, SchemaName = "DBO", ObjectName = "users", ObjectType = "TABLE", EventType = "CREATE_TABLE" }
            });

            Assert.That(names.Count, Is.EqualTo(1), "대소문자가 달라도 같은 객체다");
        }

        [Test]
        public void ToQualifiedNames_SkipsRowsWithoutAnObjectName()
        {
            var names = StateTracker.ToQualifiedNames(new[]
            {
                new ChangeLogRow { Id = 1, SchemaName = "dbo", ObjectName = "", ObjectType = "TABLE", EventType = "ALTER_TABLE" },
                new ChangeLogRow { Id = 2, SchemaName = "dbo", ObjectName = "Users", ObjectType = "TABLE", EventType = "ALTER_TABLE" }
            });

            Assert.That(names, Is.EqualTo(new[] { "dbo.Users" }));
        }

        [Test]
        public void GetChangedObjectNames_ReturnsEmpty_WhenTheDatabaseCannotBeReached()
        {
            // 접속 실패를 "바뀐 것이 없다"로 뭉개면 안 되지만, 예외를 던져 새로고침을 통째로
            // 무너뜨려서도 안 된다. 호출자(ViewModel)가 전체 추출로 되돌릴 수 있도록 빈 목록을 준다.
            var config = NewIsolatedConfig();
            var tracker = new StateTracker(config);

            IReadOnlyList<string>? names = null;
            Assert.DoesNotThrow(() => names = tracker.GetChangedObjectNames("localhost", "nonexistent_db_xyz"));
            Assert.That(names, Is.Empty);
        }

        [Test]
        public void InitializeDatabase_ThrowsArgumentException_WhenServerOrDatabaseIsEmpty()
        {
            Assert.Throws<System.ArgumentException>(() => NewTracker().InitializeDatabase("", "db"));
            Assert.Throws<System.ArgumentException>(() => NewTracker().InitializeDatabase("server", ""));
        }

        [Test]
        public void InitializeDatabase_LoadsEmbeddedScriptAndAttemptsConnection()
        {
            // 접속은 실패하지만, 그 전에 임베디드 스크립트를 읽는 데 성공해야 한다.
            // FileNotFoundException이 나오면 리소스 이름이 어긋난 것이다.
            var tracker = NewTracker();
            var ex = Assert.Catch<System.Exception>(() => tracker.InitializeDatabase("no_such_server_hostname", "no_such_db"));
            Assert.That(ex, Is.Not.InstanceOf<System.IO.FileNotFoundException>());
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
            var batches = StateTracker.SplitSqlBatches(StateTracker.ReadInstallScript());

            var triggerBatches = batches
                .Where(b => b.IndexOf("CREATE TRIGGER", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            Assert.That(triggerBatches, Is.Not.Empty);
            foreach (var batch in triggerBatches)
            {
                Assert.That(batch.TrimStart(), Does.StartWith("CREATE TRIGGER").IgnoreCase);
            }
        }

        [Test]
        public void InstallScript_CreatesChangeLogWithSyncAndSchemaColumns()
        {
            var script = StateTracker.ReadInstallScript();

            Assert.That(script, Does.Contain("IsProcessed"));
            Assert.That(script, Does.Contain("SchemaName"));
        }

        [Test]
        public void InstallScript_IsIdempotentForExistingInstallations()
        {
            var script = StateTracker.ReadInstallScript();

            Assert.That(script, Does.Contain("ALTER TABLE").IgnoreCase);
            Assert.That(script, Does.Contain("sys.columns").IgnoreCase);
        }

        // ---------- 인덱스 이벤트 정규화 ----------

        private static ChangeLogRow IndexRow(string eventType, string indexName, string? targetName = "Users")
            => new ChangeLogRow
            {
                Id = 10,
                SchemaName = "dbo",
                ObjectName = indexName,
                ObjectType = "INDEX",
                EventType = eventType,
                TargetObjectName = targetName,
                TargetObjectType = targetName == null ? null : "TABLE"
            };

        [Test]
        public void NormalizeRow_TreatsADroppedIndexAsAModifiedParentTable_NotADeletedObject()
        {
            // 이름만 바꾸고 이벤트를 그대로 두면 상태가 Deleted가 되고, WorkingTreeCleaner가
            // 그것을 보고 테이블의 .sql을 지운다 - 인덱스를 지웠을 뿐인데 저장소에서 테이블이 사라진다.
            var normalized = StateTracker.NormalizeRow(IndexRow("DROP_INDEX", "IX_Users_Name"));

            Assert.Multiple(() =>
            {
                Assert.That(normalized.ObjectName, Is.EqualTo("Users"));
                Assert.That(normalized.ObjectType, Is.EqualTo("TABLE"));
                Assert.That(StateTracker.MapEventTypeToState(normalized.EventType), Is.EqualTo("Modified"));
            });
        }

        [Test]
        [TestCase("CREATE_INDEX")]
        [TestCase("ALTER_INDEX")]
        public void NormalizeRow_PointsIndexEventsAtTheParentTable(string eventType)
        {
            var normalized = StateTracker.NormalizeRow(IndexRow(eventType, "IX_Users_Name"));

            Assert.That(normalized.ObjectName, Is.EqualTo("Users"));
            Assert.That(normalized.ObjectType, Is.EqualTo("TABLE"));
        }

        [Test]
        public void NormalizeRow_LeavesTheRowAlone_WhenTheParentIsUnknown()
        {
            // v1이 남긴 행이다. 부모를 지어낼 수 없으므로 손대지 않는다.
            var normalized = StateTracker.NormalizeRow(IndexRow("CREATE_INDEX", "IX_Users_Name", targetName: null));

            Assert.That(normalized.ObjectName, Is.EqualTo("IX_Users_Name"));
        }

        [Test]
        public void NormalizeRow_LeavesNonIndexRowsAlone()
        {
            var row = Row(1, "dbo", "Users", "TABLE", "ALTER_TABLE");

            var normalized = StateTracker.NormalizeRow(row);

            Assert.That(normalized.ObjectName, Is.EqualTo("Users"));
            Assert.That(normalized.EventType, Is.EqualTo("ALTER_TABLE"));
        }

        [Test]
        public void ToQualifiedNames_YieldsTheParentTable_ForNormalizedIndexRows()
        {
            // 추출 대상 목록에도 부모가 나와야 새로고침이 테이블을 다시 스크립팅한다.
            var names = StateTracker.ToQualifiedNames(new[] { StateTracker.NormalizeRow(IndexRow("CREATE_INDEX", "IX_Users_Name")) });

            Assert.That(names, Is.EqualTo(new[] { "dbo.Users" }));
        }

        // ---------- 커밋 완료 처리 ----------

        [Test]
        public void MarkProcessedCommand_ClosesRowsThatPointAtTheObjectAsTheirParent()
        {
            // 정규화 뒤 레코드의 이름은 테이블인데 로그의 행은 인덱스 이름이다. ObjectName만 보면
            // 인덱스 행이 닫히지 않아 커밋해도 다음 새로고침에 그대로 다시 올라온다.
            var command = StateTracker.MarkProcessedCommand;

            Assert.Multiple(() =>
            {
                Assert.That(command, Does.Contain("TargetObjectName = @objectName"));
                Assert.That(command, Does.Contain("Id <= @lastLogId"), "새로고침 이후의 이벤트는 건드리지 않아야 한다");
            });
        }
    }
}
