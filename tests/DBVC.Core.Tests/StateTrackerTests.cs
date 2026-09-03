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

        // ---------- 작업자 분리 ----------

        private static ChangeLogRow RowBy(long id, string name, string? login, string? host)
            => new ChangeLogRow
            {
                Id = id, SchemaName = "dbo", ObjectName = name, ObjectType = "TABLE",
                EventType = "ALTER_TABLE", LoginName = login, HostName = host
            };

        [Test]
        public void PartitionByAuthor_KeepsOnlyTheRowsOfTheCurrentAuthor()
        {
            var result = StateTracker.PartitionByAuthor(
                new[] { RowBy(1, "Mine", "sa", "PC-A"), RowBy(2, "Theirs", "sa", "PC-B") }, "sa", "PC-A");

            Assert.That(result.Mine.Select(r => r.ObjectName), Is.EqualTo(new[] { "Mine" }));
        }

        /// <summary>
        /// 남의 것으로 판정된 경로를 따로 내야 하는 이유: 추출은 작업자를 가리지 않으므로 남의
        /// 객체도 .sql이 써지고, 그 파일이 Git에서 더럽게 보인다. 그 목록이 없으면
        /// BuildChangeSet의 Git 폴백이 방금 걸러낸 것을 그대로 도로 넣는다.
        /// </summary>
        [Test]
        public void PartitionByAuthor_ReportsPathsThatOnlyOthersTouched()
        {
            var result = StateTracker.PartitionByAuthor(
                new[] { RowBy(1, "Mine", "sa", "PC-A"), RowBy(2, "Theirs", "sa", "PC-B") }, "sa", "PC-A");

            Assert.That(result.ForeignPaths, Is.EqualTo(new[] { "dbo/Tables/Theirs.sql" }));
        }

        [Test]
        public void PartitionByAuthor_DoesNotReportAPath_WhenTheCurrentAuthorTouchedItToo()
        {
            // 같은 객체를 둘이 만졌으면 내 목록에 남아야 한다. 커밋 시점의 확인 대화상자가
            // 그 사실을 알리는 자리이지, 목록에서 지워 버릴 일이 아니다.
            var result = StateTracker.PartitionByAuthor(
                new[] { RowBy(1, "Shared", "sa", "PC-B"), RowBy(2, "Shared", "sa", "PC-A") }, "sa", "PC-A");

            Assert.That(result.ForeignPaths, Is.Empty);
        }

        [Test]
        public void PartitionByAuthor_TreatsNullAndEmptyAsTheSameValue()
        {
            // v3 이전에 쌓인 행은 HostName이 NULL이다. 이 판정은 이전에 SQL의
            // ISNULL(HostName, N'') = ISNULL(@host, N'') 이 하던 것과 같아야 한다.
            var result = StateTracker.PartitionByAuthor(
                new[] { RowBy(1, "Legacy", "sa", null) }, "sa", "");

            Assert.That(result.Mine, Has.Count.EqualTo(1));
            Assert.That(result.ForeignPaths, Is.Empty);
        }

        // ---------- 이름 변경 접기 ----------

        private static ChangeLogRow TableRow(long id, string name, string eventType, string? newName = null)
            => new ChangeLogRow
            {
                Id = id, SchemaName = "dbo", ObjectName = name, ObjectType = "TABLE",
                EventType = eventType, NewObjectName = newName
            };

        /// <summary>
        /// RENAME은 옛 이름으로 기록된다. 그대로 두면 존재하지 않는 객체가 목록에 뜨고,
        /// 정작 바뀐 객체는 로그에 없어 추출되지 않는다.
        /// </summary>
        [Test]
        public void FoldRenames_MovesTheRenameEventToTheNewName()
        {
            var folded = StateTracker.FoldRenames(new[] { TableRow(1, "p_old", "RENAME", "p_new") });

            Assert.That(folded.Single(r => r.EventType == "RENAME").ObjectName, Is.EqualTo("p_new"));
        }

        /// <summary>
        /// 이름이 바뀌기 전에 그 객체에 일어난 일도 새 이름의 이력이다. 옮기지 않으면
        /// 사라진 이름이 목록에 남는다 - SSMS 테이블 디자이너의 Tmp_ 테이블이 그것이다.
        /// </summary>
        [Test]
        public void FoldRenames_MovesEarlierEventsOfTheOldNameToTheNewName()
        {
            var folded = StateTracker.FoldRenames(new[]
            {
                TableRow(2, "Tmp_T", "RENAME", "T"),
                TableRow(1, "Tmp_T", "CREATE_TABLE")
            });

            Assert.That(folded.Where(r => !r.EventType.StartsWith("DROP")).Select(r => r.ObjectName),
                Is.All.EqualTo("T"));
        }

        /// <summary>
        /// 이름이 바뀌면 저장소의 옛 .sql은 남을 이유가 없다. 삭제 행을 내지 않으면
        /// 그 파일이 영영 고아로 남는다. 저장소에 없던 이름(Tmp_ 테이블)이면 뒤따르는
        /// DB 대조가 이 행을 걷어낸다.
        /// </summary>
        [Test]
        public void FoldRenames_EmitsADeletionForTheOldName()
        {
            var folded = StateTracker.FoldRenames(new[] { TableRow(1, "p_old", "RENAME", "p_new") });

            var vanished = folded.Single(r => r.ObjectName == "p_old");
            Assert.That(StateTracker.MapEventTypeToState(vanished.EventType), Is.EqualTo("Deleted"));
        }

        [Test]
        public void FoldRenames_FollowsAChainOfRenames()
        {
            var folded = StateTracker.FoldRenames(new[]
            {
                TableRow(3, "B", "RENAME", "C"),
                TableRow(2, "A", "RENAME", "B"),
                TableRow(1, "A", "CREATE_TABLE")
            });

            Assert.That(folded.Where(r => !r.EventType.StartsWith("DROP")).Select(r => r.ObjectName), Is.All.EqualTo("C"));
        }

        /// <summary>
        /// 같은 이름이 두 번 쓰이면 홉을 고를 때 순서가 갈린다. 옛 이름의 행은 그 이름이
        /// <em>처음</em> 비워졌을 때 따라가야 한다 - 나중의 이름 변경은 그때 만들어진 다른
        /// 객체의 이야기다. 최신 것을 먼저 집으면 서로 다른 두 객체가 한 항목으로 뭉친다.
        /// </summary>
        [Test]
        public void FoldRenames_FollowsTheEarliestRename_WhenTheOldNameWasReused()
        {
            var folded = StateTracker.FoldRenames(new[]
            {
                TableRow(30, "A", "RENAME", "C"),
                TableRow(20, "A", "CREATE_TABLE"),
                TableRow(10, "A", "RENAME", "B"),
                TableRow(5, "A", "ALTER_TABLE")
            });

            Assert.That(folded.Single(r => r.Id == 5 && r.EventType == "ALTER_TABLE").ObjectName,
                Is.EqualTo("B"), "Id 10에서 B가 된 원래 객체의 이력이다");
            Assert.That(folded.Single(r => r.Id == 20).ObjectName,
                Is.EqualTo("C"), "Id 20에서 새로 만든 객체는 Id 30에서 C가 된다");
        }

        /// <summary>
        /// 컬럼 이름 변경은 부모 테이블의 수정으로 정규화된다. 그때 컬럼의 새 이름이 따라
        /// 올라오면 테이블이 컬럼 이름으로 접힌다 - 지금은 NormalizeRow가 그 속성을 옮기지
        /// 않는 것으로만 지켜지고 있어, 복사 헬퍼로 바뀌면 조용히 깨진다.
        /// </summary>
        [Test]
        public void NormalizeRow_DoesNotCarryTheNewNameToTheParent_ForAColumnRename()
        {
            var normalized = StateTracker.NormalizeRow(new ChangeLogRow
            {
                Id = 1, SchemaName = "dbo", ObjectName = "OldColumn", ObjectType = "COLUMN",
                EventType = "RENAME", NewObjectName = "NewColumn",
                TargetObjectName = "Orders", TargetObjectType = "TABLE"
            });

            Assert.That(normalized.ObjectName, Is.EqualTo("Orders"));
            Assert.That(normalized.NewObjectName, Is.Null,
                "컬럼의 새 이름을 테이블 행에 남기면 FoldRenames가 테이블을 컬럼 이름으로 접는다");
        }

        /// <summary>
        /// 입력 행을 제자리에서 고치면 두 번 부를 때 두 번 접힌다. 호출자가 같은 목록을
        /// 다시 쓰는 것을 막을 방법이 없으므로 새 행을 낸다.
        /// </summary>
        [Test]
        public void FoldRenames_DoesNotMutateTheRowsItWasGiven()
        {
            var input = new[] { TableRow(1, "p_old", "RENAME", "p_new") };

            StateTracker.FoldRenames(input);

            Assert.That(input[0].ObjectName, Is.EqualTo("p_old"));
        }

        /// <summary>
        /// 이름이 비어 있는 행 하나가 새로고침 전체를 무너뜨려서는 안 된다.
        /// 다른 소비자들은 모두 먼저 걸러 낸다.
        /// </summary>
        [Test]
        public void FoldRenames_SkipsRowsWithNoObjectName()
        {
            Assert.DoesNotThrow(() => StateTracker.FoldRenames(new[]
            {
                TableRow(2, "T", "RENAME", "U"),
                TableRow(1, "   ", "ALTER_TABLE")
            }));
        }

        /// <summary>
        /// v4 이전에 쌓인 RENAME 행은 새 이름을 담고 있지 않다. 지어낼 근거가 없으므로 둔다 -
        /// 그런 행이 남긴 유령 항목은 DB 대조(ReconcileWithDatabase)가 걷어낸다.
        /// </summary>
        [Test]
        public void FoldRenames_LeavesTheRowAlone_WhenTheNewNameIsUnknown()
        {
            var folded = StateTracker.FoldRenames(new[] { TableRow(1, "Tmp_T", "RENAME") });

            Assert.That(folded.Single().ObjectName, Is.EqualTo("Tmp_T"),
                "새 이름을 모르면 옮길 곳도, 삭제로 볼 근거도 없다");
        }

        /// <summary>
        /// 이름을 비운 뒤 같은 이름으로 새 객체를 만들 수 있다. 그 뒤의 행까지 옮기면
        /// 서로 다른 두 객체가 한 항목으로 뭉친다.
        /// </summary>
        [Test]
        public void FoldRenames_DoesNotMoveRowsRecordedAfterTheRename()
        {
            var folded = StateTracker.FoldRenames(new[]
            {
                TableRow(3, "A", "CREATE_TABLE"),
                TableRow(2, "A", "RENAME", "B")
            });

            Assert.That(folded.Single(r => r.EventType == "CREATE_TABLE").ObjectName, Is.EqualTo("A"));
        }

        /// <summary>
        /// 실제로 터진 경로. SSMS 테이블 디자이너로 열 형식만 바꾸면 이 네 행이 남는다.
        /// 접기가 없으면 Table_3의 최신 이벤트가 DROP_TABLE이라 살아 있는 테이블이 삭제로 뜨고,
        /// 커밋하면 WorkingTreeCleaner가 저장소에서 그 .sql을 지운다.
        /// </summary>
        [Test]
        public void BuildChangeSet_ReportsTheLiveTableAsModified_WhenTheDesignerRebuiltIt()
        {
            var tracker = NewTracker();
            var folded = StateTracker.FoldRenames(new[]
            {
                TableRow(17, "Tmp_Table_3", "RENAME", "Table_3"),
                TableRow(16, "Table_3", "DROP_TABLE"),
                TableRow(15, "Tmp_Table_3", "ALTER_TABLE"),
                TableRow(14, "Tmp_Table_3", "CREATE_TABLE")
            });

            // 생산 흐름과 같은 순서다. 대조가 없으면 사라진 Tmp_ 이름이 삭제 항목으로 남는다.
            var changes = StateTracker.ReconcileWithDatabase(
                tracker.BuildChangeSet(folded, null),
                existingQualifiedNames: new[] { "dbo.Table_3" },
                hasRepositoryFile: path => path == "dbo/Tables/Table_3.sql");

            Assert.That(changes.Select(c => c.ObjectName), Is.EqualTo(new[] { "Table_3" }),
                "Tmp_ 테이블은 존재한 적이 없는 이름이라 목록에 남으면 안 된다");
            Assert.That(changes.Single().State, Is.EqualTo("Modified"),
                "살아 있는 테이블이 삭제로 뜨면 커밋 시점에 저장소에서 .sql이 지워진다");
        }

        // ---------- DB 대조 ----------

        private static ChangeRecord Record(string name, string state, long logId = 1)
            => new ChangeRecord
            {
                Schema = "dbo", ObjectName = name, ObjectType = "TABLE", State = state,
                QualifiedName = "dbo." + name, RelativePath = "dbo/Tables/" + name + ".sql", LastLogId = logId
            };

        /// <summary>
        /// 살아 있는 객체는 삭제일 수 없다. v4 이전에 쌓인 디자이너 재작성 행이 정확히 이 모양이다.
        /// </summary>
        [Test]
        public void ReconcileWithDatabase_TurnsDeletedIntoModified_WhenTheObjectStillExists()
        {
            var reconciled = StateTracker.ReconcileWithDatabase(
                new[] { Record("Table_3", "Deleted") },
                existingQualifiedNames: new[] { "dbo.Table_3" },
                hasRepositoryFile: _ => true);

            Assert.That(reconciled.Single().State, Is.EqualTo("Modified"));
        }

        /// <summary>
        /// DB에도 없고 저장소에도 없으면 보여 줄 것도 커밋할 것도 없다. 비교창이 빈 채로 뜬다.
        /// </summary>
        [Test]
        public void ReconcileWithDatabase_DropsRecords_ThatExistInNeitherTheDatabaseNorTheRepository()
        {
            var reconciled = StateTracker.ReconcileWithDatabase(
                new[] { Record("Tmp_Table_3", "Modified") },
                existingQualifiedNames: System.Array.Empty<string>(),
                hasRepositoryFile: _ => false);

            Assert.That(reconciled, Is.Empty);
        }

        /// <summary>
        /// 진짜로 지워진 객체는 DB에 없지만 저장소에는 .sql이 남아 있다. 그것까지 걷어내면
        /// 삭제가 영영 커밋되지 않는다.
        /// </summary>
        [Test]
        public void ReconcileWithDatabase_KeepsDeletedRecords_WhenTheRepositoryStillHasTheFile()
        {
            var reconciled = StateTracker.ReconcileWithDatabase(
                new[] { Record("Gone", "Deleted") },
                existingQualifiedNames: System.Array.Empty<string>(),
                hasRepositoryFile: _ => true);

            Assert.That(reconciled.Single().State, Is.EqualTo("Deleted"));
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

        /// <summary>
        /// Git 폴백이 작업자 필터를 새게 하던 자리. 남의 미처리 변경도 추출되어 파일이 더러워지므로,
        /// 폴백을 그대로 두면 걸러낸 항목이 전부 목록으로 돌아왔다.
        /// </summary>
        [Test]
        public void BuildChangeSet_OmitsDirtyFiles_WhenTheirOnlyLogRowsBelongToSomeoneElse()
        {
            var tracker = NewTracker();
            var gitStates = new Dictionary<string, string> { ["dbo/Tables/Theirs.sql"] = "Modified" };

            var changes = tracker.BuildChangeSet(
                System.Array.Empty<ChangeLogRow>(), gitStates, new[] { "dbo/Tables/Theirs.sql" });

            Assert.That(changes, Is.Empty);
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
        public void RequiredSchemaVersion_IsFive()
        {
            // 설치 스크립트가 심는 값과 같아야 한다. 어긋나면 모든 사용자에게 업데이트 배너가 계속 뜨거나
            // 구버전이 최신으로 읽힌다. 스크립트 쪽 값은 InstallScriptSyncTests가 대조한다.
            Assert.That(StateTracker.RequiredSchemaVersion, Is.EqualTo(5));
        }

        [Test]
        public void MarkProcessedFailureMessage_SaysTheCommitSucceededAndTheItemWillReturn()
        {
            // 이 안내가 "커밋 실패"로 읽히면 사용자가 같은 커밋을 다시 만든다.
            // 항목이 목록에 되살아나는 것도 미리 말해야 결함으로 신고되지 않는다.
            var message = StateTracker.BuildMarkProcessedFailureMessage("SELECT 권한이 거부되었습니다");

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("커밋은 성공"));
                Assert.That(message, Does.Contain("다시 나타납니다"));
                Assert.That(message, Does.Contain("SELECT 권한이 거부되었습니다"));
            });
        }

        [Test]
        public void MarkProcessedFailureMessage_PointsAtThePermissionAndTheButtonThatFixesIt()
        {
            // 원인이 거의 항상 권한이고, 고치는 자리가 화면 안에 있다. 그 두 가지를 말하지 않으면
            // 사용자는 libgit2/서버 원문만 보고 무엇을 해야 할지 모른다.
            var message = StateTracker.BuildMarkProcessedFailureMessage("무엇이든");

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("DBVC_ChangeLog"));
                Assert.That(message, Does.Contain("UPDATE"));
                Assert.That(message, Does.Contain("db_owner"));
                Assert.That(message, Does.Contain("변경 추적기 업데이트"));
            });
        }

        [Test]
        [TestCase("other", "PC-A", TestName = "PartitionByAuthor_SeparatesByLogin")]
        [TestCase("sa", "PC-B", TestName = "PartitionByAuthor_SeparatesByHost")]
        public void PartitionByAuthor_UsesBothLoginAndHost(string rowLogin, string rowHost)
        {
            // 공용 계정 환경에서는 LoginName이 상수라 HostName이 일을 한다.
            // 계정을 사람별로 나눈 환경에서는 둘 다 의미가 있다. 규칙을 두 번 만들지 않는다.
            var result = StateTracker.PartitionByAuthor(
                new[] { RowBy(1, "T", rowLogin, rowHost) }, "sa", "PC-A");

            Assert.That(result.Mine, Is.Empty);
            Assert.That(result.ForeignPaths, Has.Count.EqualTo(1));
        }

        [Test]
        public void PendingChangesQuery_SelectsAuthorColumns()
        {
            // 전체 보기에서도 변경자 컬럼을 띄워야 하므로 필터 없는 쪽도 값을 읽어야 한다.
            Assert.That(StateTracker.PendingChangesQuery, Does.Contain("LoginName"));
            Assert.That(StateTracker.PendingChangesQuery, Does.Contain("HostName"));
        }

        [Test]
        public void MarkProcessedCommand_NarrowsByAuthor()
        {
            // 같은 객체를 둘이 만졌을 때, A의 커밋이 B의 로그까지 닫으면
            // B 화면에서 조용히 사라진다(설계 1.3).
            Assert.That(StateTracker.MarkProcessedCommand, Does.Contain("@login"));
            Assert.That(StateTracker.MarkProcessedCommand, Does.Contain("@host"));
        }

        [Test]
        public void MarkProcessedCommand_TreatsNullHostAsEmpty()
        {
            // v3 이전 행은 HostName이 NULL이다. NULL = @host는 절대 참이 되지 않으므로
            // 전체 보기에서 그 행을 커밋해도 닫히지 않고 매번 다시 올라온다.
            Assert.That(StateTracker.MarkProcessedCommand, Does.Contain("ISNULL(HostName"));
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

        private static ChangeLogRow ColumnRow(string columnName, string? targetName = "Users")
            => new ChangeLogRow
            {
                Id = 11,
                SchemaName = "dbo",
                ObjectName = columnName,
                ObjectType = "COLUMN",
                EventType = "RENAME",
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
        public void NormalizeRow_KeepsTheParentType_WhenTheIndexIsOnAnIndexedView()
        {
            // 인덱싱된 뷰의 인덱스는 TargetObjectType이 VIEW로 온다(실측). 타입을 TABLE로 못박으면
            // 그 뷰가 dbo/Tables/... 로 떨어져 저장소의 실제 파일과 다른 경로를 보게 된다.
            var row = IndexRow("CREATE_INDEX", "IX_vUsers");
            row.TargetObjectName = "vUsers";
            row.TargetObjectType = "VIEW";

            var normalized = StateTracker.NormalizeRow(row);

            Assert.Multiple(() =>
            {
                Assert.That(normalized.ObjectName, Is.EqualTo("vUsers"));
                Assert.That(normalized.ObjectType, Is.EqualTo("VIEW"));
            });
        }

        [Test]
        public void NormalizeRow_PointsColumnEventsAtTheParentTable()
        {
            // sp_rename으로 컬럼 이름을 바꾸면 COLUMN 이벤트 하나만 남고 테이블 이벤트는 생기지 않는다.
            // 부모로 옮기지 않으면 그 변경은 저장소에 영영 반영되지 않는다.
            var normalized = StateTracker.NormalizeRow(ColumnRow("Name"));

            Assert.Multiple(() =>
            {
                Assert.That(normalized.ObjectName, Is.EqualTo("Users"));
                Assert.That(normalized.ObjectType, Is.EqualTo("TABLE"));
                Assert.That(StateTracker.MapEventTypeToState(normalized.EventType), Is.EqualTo("Modified"));
            });
        }

        [Test]
        public void NormalizeRow_LeavesTheColumnRowAlone_WhenTheParentIsUnknown()
        {
            var normalized = StateTracker.NormalizeRow(ColumnRow("Name", targetName: null));

            Assert.That(normalized.ObjectName, Is.EqualTo("Name"));
        }

        [Test]
        public void ToQualifiedNames_YieldsTheParentTable_ForNormalizedColumnRows()
        {
            var names = StateTracker.ToQualifiedNames(new[] { StateTracker.NormalizeRow(ColumnRow("Name")) });

            Assert.That(names, Is.EqualTo(new[] { "dbo.Users" }));
        }

        [Test]
        public void ToQualifiedNames_YieldsTheParentTable_ForNormalizedIndexRows()
        {
            // 추출 대상 목록에도 부모가 나와야 새로고침이 테이블을 다시 스크립팅한다.
            var names = StateTracker.ToQualifiedNames(new[] { StateTracker.NormalizeRow(IndexRow("CREATE_INDEX", "IX_Users_Name")) });

            Assert.That(names, Is.EqualTo(new[] { "dbo.Users" }));
        }

    }
}
