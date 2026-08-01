using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class ViewChangesViewModelTests
    {
        private const string Server = "LocalServer";
        private const string Database = "SalesDB";

        private Mock<IConfigManager> _config = null!;
        private Mock<IStateTracker> _stateTracker = null!;
        private Mock<IGitManager> _git = null!;
        private Mock<ISmoManager> _smo = null!;
        private RecordingNotifier _notifier = null!;
        private RecordingSaveDialog _saveDialog = null!;
        private Mock<IWorkingTreeCleaner> _cleaner = null!;
        private RecordingFolderDialog _folderDialog = null!;
        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            _tempDirs.Clear();
        }

        [SetUp]
        public void SetUp()
        {
            _saveDialog = new RecordingSaveDialog();
            _folderDialog = new RecordingFolderDialog();
            _config = new Mock<IConfigManager>();
            _stateTracker = new Mock<IStateTracker>();
            _git = new Mock<IGitManager>();
            _smo = new Mock<ISmoManager>();
            _notifier = new RecordingNotifier();

            // 기본값: 매핑되어 있고 초기화되어 있으며 변경 없음
            _config.Setup(c => c.TryGetMapping(Server, Database))
                .Returns(new MappingConfig { ServerName = Server, DatabaseName = Database, GitPath = @"C:\repo" });
            _stateTracker.Setup(s => s.IsInitialized(It.IsAny<string>())).Returns(true);
            _stateTracker.Setup(s => s.RefreshState(Server, Database)).Returns(true);
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>());
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null)).Returns(new ScriptResult());

            _cleaner = new Mock<IWorkingTreeCleaner>();
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(new CleanupResult());
        }

        private ViewChangesViewModel NewViewModel()
        {
            return new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _folderDialog);
        }

        private ViewChangesViewModel NewConnectedViewModel()
        {
            var vm = NewViewModel();
            vm.SetContext(Server, Database);
            return vm;
        }

        private static ChangeRecord Record(string schema, string name, string state, string path)
            => new ChangeRecord
            {
                Schema = schema,
                ObjectName = name,
                State = state,
                QualifiedName = $"{schema}.{name}",
                RelativePath = path,
                LastLogId = 1
            };

        // ---------- 기본 상태 ----------

        [Test]
        public void IsInitialized_DefaultsToFalse()
        {
            Assert.That(NewViewModel().IsInitialized, Is.False);
        }

        [Test]
        public void CommitMessage_CanBeSetAndRetrieved()
        {
            var vm = NewViewModel();
            vm.CommitMessage = "Test commit";
            Assert.That(vm.CommitMessage, Is.EqualTo("Test commit"));
        }

        [Test]
        public void Changes_StartsEmpty()
        {
            var vm = NewViewModel();
            Assert.That(vm.Changes, Is.Not.Null);
            Assert.That(vm.Changes, Is.Empty);
        }

        [Test]
        public void SelectedChange_CanBeSetAndRetrieved()
        {
            var vm = NewViewModel();
            var item = new ChangeItemViewModel { ObjectName = "dbo.Table1", State = "Modified" };

            vm.SelectedChange = item;

            Assert.That(vm.SelectedChange, Is.SameAs(item));
        }

        // ---------- 컨텍스트 설정 ----------

        [Test]
        public void SetContext_ReadsInitializationStateFromStateTracker()
        {
            var vm = NewConnectedViewModel();

            _stateTracker.Verify(s => s.IsInitialized(It.IsAny<string>()), Times.Once);
            Assert.That(vm.IsInitialized, Is.True);
        }

        [Test]
        public void SetContext_MarksNotInitialized_WhenTrackerSaysSo()
        {
            _stateTracker.Setup(s => s.IsInitialized(It.IsAny<string>())).Returns(false);

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsInitialized, Is.False, "설치되지 않은 DB에서는 Setup 오버레이가 보여야 합니다");
        }

        [Test]
        public void SetContext_ReportsUnmappedDatabase_WithAProminentWarning()
        {
            // 설계: "Active Database is not mapped to a Git repository." 경고 표시 + 커밋 비활성화
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsMapped, Is.False);
            Assert.That(vm.WarningMessage, Does.Contain("not mapped"));
        }

        [Test]
        public void SetContext_ClearsWarning_WhenDatabaseIsMapped()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.IsMapped, Is.True);
            Assert.That(vm.WarningMessage, Is.Null.Or.Empty);
        }

        [Test]
        public void ConnectCommand_AppliesTheEnteredServerAndDatabase()
        {
            var vm = NewViewModel();
            vm.ServerName = Server;
            vm.DatabaseName = Database;

            vm.ConnectCommand.Execute(null);

            Assert.That(vm.IsMapped, Is.True);
            _stateTracker.Verify(s => s.IsInitialized(It.IsAny<string>()), Times.Once);
        }

        [Test]
        public void ConnectCommand_CannotExecute_UntilBothServerAndDatabaseAreEntered()
        {
            var vm = NewViewModel();
            Assert.That(vm.ConnectCommand.CanExecute(null), Is.False);

            vm.ServerName = Server;
            Assert.That(vm.ConnectCommand.CanExecute(null), Is.False);

            vm.DatabaseName = Database;
            Assert.That(vm.ConnectCommand.CanExecute(null), Is.True);
        }

        // ---------- Setup ----------

        [Test]
        public void SetupCommand_InstallsTheChangeLogAndTrigger()
        {
            _stateTracker.Setup(s => s.IsInitialized(It.IsAny<string>())).Returns(false);
            var vm = NewConnectedViewModel();

            vm.SetupCommand.Execute(null);

            _stateTracker.Verify(s => s.InitializeDatabase(It.Is<string>(cs => cs.Contains(Database))), Times.Once);
            Assert.That(vm.IsInitialized, Is.True);
        }

        [Test]
        public void SetupCommand_RefreshesAfterSuccessfulInstall()
        {
            _stateTracker.Setup(s => s.IsInitialized(It.IsAny<string>())).Returns(false);
            var vm = NewConnectedViewModel();

            vm.SetupCommand.Execute(null);

            _stateTracker.Verify(s => s.RefreshState(Server, Database), Times.AtLeastOnce);
        }

        [Test]
        public void SetupCommand_KeepsOverlayVisibleAndNotifies_WhenInstallationFails()
        {
            // 권한 부족(db_owner 아님) 등으로 설치가 실패하면 초기화되었다고 주장해서는 안 된다.
            _stateTracker.Setup(s => s.IsInitialized(It.IsAny<string>())).Returns(false);
            _stateTracker.Setup(s => s.InitializeDatabase(It.IsAny<string>()))
                .Throws(new InvalidOperationException("권한이 없습니다"));
            var vm = NewConnectedViewModel();

            vm.SetupCommand.Execute(null);

            Assert.That(vm.IsInitialized, Is.False, "설치에 실패했는데 오버레이를 숨기면 안 됩니다");
            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Does.Contain("권한이 없습니다"));
        }

        [Test]
        public void SetupCommand_DoesNothing_WhenNoDatabaseContextIsSet()
        {
            var vm = NewViewModel(); // SetContext 호출 안 함

            vm.SetupCommand.Execute(null);

            _stateTracker.Verify(s => s.InitializeDatabase(It.IsAny<string>()), Times.Never);
            Assert.That(vm.IsInitialized, Is.False);
        }

        // ---------- Refresh ----------

        [Test]
        public void RefreshCommand_PopulatesChangesFromStateTracker()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"),
                Record("dbo", "vw_Users", "Added", "dbo/Views/vw_Users.sql")
            });
            var vm = NewConnectedViewModel();

            vm.RefreshCommand.Execute(null);

            Assert.That(vm.Changes.Select(c => c.ObjectName), Is.EqualTo(new[] { "dbo.Users", "dbo.vw_Users" }));
            Assert.That(vm.Changes[0].State, Is.EqualTo("Modified"));
            Assert.That(vm.Changes[1].RelativePath, Is.EqualTo("dbo/Views/vw_Users.sql"));
        }

        [Test]
        public void RefreshCommand_ExportsObjectsWithSmoBeforeReadingState()
        {
            // Diff와 커밋이 최신 DB 코드를 보려면 새로고침 시 스크립트 추출이 선행되어야 한다.
            var sequence = new List<string>();
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null))
                .Callback(() => sequence.Add("script"))
                .Returns(new ScriptResult());
            _stateTracker.Setup(s => s.RefreshState(Server, Database))
                .Callback(() => sequence.Add("refresh"))
                .Returns(true);
            var vm = NewConnectedViewModel();
            sequence.Clear();

            vm.RefreshCommand.Execute(null);

            Assert.That(sequence, Is.EqualTo(new[] { "script", "refresh" }));
        }

        [Test]
        public void RefreshCommand_ClearsPreviousItems()
        {
            var vm = NewConnectedViewModel();
            vm.Changes.Add(new ChangeItemViewModel { ObjectName = "dbo.Stale" });

            vm.RefreshCommand.Execute(null);

            Assert.That(vm.Changes, Is.Empty);
        }

        [Test]
        public void RefreshCommand_SkipsWorkAndWarns_WhenDatabaseIsNotMapped()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            var vm = NewConnectedViewModel();

            vm.RefreshCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>()), Times.Never);
            Assert.That(vm.WarningMessage, Does.Contain("not mapped"));
        }

        [Test]
        public void RefreshCommand_ReportsPartialScriptingFailures()
        {
            var result = new ScriptResult { SucceededCount = 3 };
            result.FailedObjects.Add("dbo.Broken");
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null)).Returns(result);
            var vm = NewConnectedViewModel();

            vm.RefreshCommand.Execute(null);

            Assert.That(vm.WarningMessage, Does.Contain("dbo.Broken"),
                "일부 객체 추출 실패는 조용히 무시되면 안 됩니다");
        }

        // ---------- 삭제된 객체의 작업 트리 정리 ----------

        [Test]
        public void Refresh_RemovesWorkingTreeFilesForDroppedObjects()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database))
                .Returns(new List<ChangeRecord> { Record("dbo", "Users", "Deleted", "dbo/Tables/Users.sql") });

            NewConnectedViewModel();

            _cleaner.Verify(
                c => c.RemoveDeletedObjectFiles(
                    @"C:\repo",
                    It.Is<IEnumerable<ChangeRecord>>(records => records.Any(r => r.RelativePath == "dbo/Tables/Users.sql"))),
                Times.AtLeastOnce,
                "파일이 남으면 Git이 삭제를 감지하지 못해 커밋되지 않습니다");
        }

        [Test]
        public void Refresh_WarnsWhenADroppedObjectFileCannotBeRemoved()
        {
            var failed = new CleanupResult();
            failed.FailedPaths.Add("dbo/Tables/Users.sql");
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(failed);

            var vm = NewConnectedViewModel();

            Assert.That(vm.WarningMessage, Does.Contain("dbo/Tables/Users.sql"));
        }

        [Test]
        public void Refresh_DoesNotWarn_WhenNothingFailedToBeRemoved()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.WarningMessage, Is.Null);
        }

        // ---------- 저장소 매핑 등록 ----------

        [Test]
        public void ConnectRepositoryCommand_IsEnabled_OnlyWhenTheDatabaseIsNotYetMapped()
        {
            var mapped = NewConnectedViewModel();
            Assert.That(mapped.ConnectRepositoryCommand.CanExecute(null), Is.False,
                "이미 매핑되어 있으면 저장소를 다시 연결할 이유가 없습니다");

            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            var unmapped = NewConnectedViewModel();
            Assert.That(unmapped.ConnectRepositoryCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void ConnectRepositoryCommand_SavesTheMapping_WhenTheChosenFolderIsAGitRepository()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _git.Setup(g => g.IsRepository(@"C:\chosen-repo")).Returns(true);
            _folderDialog.PathToReturn = @"C:\chosen-repo";
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(Server, Database, @"C:\chosen-repo"), Times.Once);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNothing_WhenTheUserCancels()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _folderDialog.PathToReturn = null;
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(_folderDialog.CallCount, Is.EqualTo(1));
            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Empty, "취소는 오류가 아닙니다");
        }

        [Test]
        public void ConnectRepositoryCommand_RefusesAFolderThatIsNotAGitRepository()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _git.Setup(g => g.IsRepository(It.IsAny<string>())).Returns(false);
            _folderDialog.PathToReturn = @"C:\not-a-repo";
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Not.Empty,
                "유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패합니다");
        }

        // ---------- Commit ----------

        [Test]
        public void CommitCommand_CommitsOnlyTheCheckedItems()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"),
                Record("dbo", "Orders", "Modified", "dbo/Tables/Orders.sql")
            });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(true);
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);

            vm.Changes.Single(c => c.ObjectName == "dbo.Users").IsSelected = true;
            vm.Changes.Single(c => c.ObjectName == "dbo.Orders").IsSelected = false;
            vm.CommitMessage = "Only users";

            vm.CommitCommand.Execute(null);

            _git.Verify(g => g.CommitChanges(Server, Database, "Only users",
                It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { "dbo/Tables/Users.sql" }))), Times.Once);
        }

        [Test]
        public void CommitCommand_MarksChangesProcessedAndRefreshes_OnSuccess()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql")
            });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(true);
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            vm.Changes[0].IsSelected = true;
            vm.CommitMessage = "msg";

            vm.CommitCommand.Execute(null);

            _stateTracker.Verify(s => s.MarkProcessed(Server, Database, It.IsAny<IEnumerable<ChangeRecord>>()), Times.Once);
            Assert.That(vm.CommitMessage, Is.Empty, "커밋 성공 후 메시지 입력창은 비워져야 합니다");
        }

        [Test]
        public void CommitCommand_Notifies_WhenCommitThrows()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql")
            });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Throws(new InvalidOperationException("저장소가 잠겨 있습니다"));
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            vm.Changes[0].IsSelected = true;
            vm.CommitMessage = "msg";

            Assert.DoesNotThrow(() => vm.CommitCommand.Execute(null));

            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Does.Contain("저장소가 잠겨 있습니다"));
        }

        [Test]
        public void CommitCommand_CannotExecute_WhenNothingIsChecked()
        {
            var vm = NewConnectedViewModel();
            vm.CommitMessage = "msg";

            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void CommitCommand_CannotExecute_WhenCommitMessageIsEmpty()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql")
            });
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            vm.Changes[0].IsSelected = true;
            vm.CommitMessage = "   ";

            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void CommitCommand_CannotExecute_WhenDatabaseIsNotMapped()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            var vm = NewConnectedViewModel();
            vm.CommitMessage = "msg";

            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void CommitCommand_CanExecute_WhenItemsCheckedAndMessagePresent()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql")
            });
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            vm.Changes[0].IsSelected = true;
            vm.CommitMessage = "msg";

            Assert.That(vm.CommitCommand.CanExecute(null), Is.True);
        }

        // ---------- SQL 에디터에서 객체 선택 (Feature 11/12) ----------

        private ViewChangesViewModel NewViewModelWithChanges(params ChangeRecord[] records)
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(records.ToList());
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            return vm;
        }

        [Test]
        public void TrySelectObject_SelectsTheMatchingChangeItem()
        {
            var vm = NewViewModelWithChanges(
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"),
                Record("sales", "Orders", "Modified", "sales/Tables/Orders.sql"));

            var selected = vm.TrySelectObject("sales", "Orders");

            Assert.That(selected, Is.True);
            Assert.That(vm.SelectedChange!.ObjectName, Is.EqualTo("sales.Orders"));
        }

        [Test]
        public void TrySelectObject_PrefersDbo_WhenTheSchemaIsNotSpecified()
        {
            var vm = NewViewModelWithChanges(
                Record("app", "Users", "Modified", "app/Tables/Users.sql"),
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"));

            Assert.That(vm.TrySelectObject(null, "Users"), Is.True);
            Assert.That(vm.SelectedChange!.ObjectName, Is.EqualTo("dbo.Users"));
        }

        [Test]
        public void TrySelectObject_ReturnsFalse_WhenTheObjectHasNoPendingChange()
        {
            var vm = NewViewModelWithChanges(Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"));

            Assert.That(vm.TrySelectObject("dbo", "Nope"), Is.False);
            Assert.That(vm.SelectedChange, Is.Null, "찾지 못했으면 기존 선택을 바꾸지 않아야 합니다");
        }

        [Test]
        public void TrySelectObject_RaisesSelectionChanged_SoTheDiffViewRefreshes()
        {
            var vm = NewViewModelWithChanges(Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql"));
            int raised = 0;
            vm.SelectionChanged += (_, __) => raised++;

            vm.TrySelectObject("dbo", "Users");

            Assert.That(raised, Is.EqualTo(1));
        }

        // ---------- Deployment / Rollback 스크립트 ----------

        /// <summary>변경 목록 1건이 있고 작업 트리에 해당 파일이 있는 VM을 만든다.</summary>
        private ViewChangesViewModel NewViewModelWithOneCheckedChange(out string repoPath)
        {
            repoPath = Path.Combine(Path.GetTempPath(), "dbvc_vm_" + Guid.NewGuid().ToString("N"));
            var sqlPath = Path.Combine(repoPath, "dbo", "Tables", "Users.sql");
            Directory.CreateDirectory(Path.GetDirectoryName(sqlPath)!);
            File.WriteAllText(sqlPath, "CREATE TABLE Users (Id INT);");
            _tempDirs.Add(repoPath);

            _config.Setup(c => c.TryGetMapping(Server, Database))
                .Returns(new MappingConfig { ServerName = Server, DatabaseName = Database, GitPath = repoPath });
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql")
            });

            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            vm.Changes[0].IsSelected = true;
            return vm;
        }

        [Test]
        public void GenerateDeploymentScriptCommand_WritesTheMergedScriptToTheChosenPath()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            var outputPath = Path.Combine(repoPath, "deploy.sql");
            _saveDialog.PathToReturn = outputPath;

            vm.GenerateDeploymentScriptCommand.Execute(null);

            Assert.That(File.Exists(outputPath), Is.True);
            var script = File.ReadAllText(outputPath);
            Assert.That(script, Does.Contain("DBVC Deployment Script"));
            Assert.That(script, Does.Contain("CREATE TABLE Users (Id INT);"));
        }

        [Test]
        public void GenerateDeploymentScriptCommand_DoesNothing_WhenTheUserCancelsTheSaveDialog()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            _saveDialog.PathToReturn = null; // 취소

            Assert.DoesNotThrow(() => vm.GenerateDeploymentScriptCommand.Execute(null));

            Assert.That(Directory.GetFiles(repoPath, "*.sql", SearchOption.TopDirectoryOnly), Is.Empty);
            Assert.That(_notifier.Errors, Is.Empty, "취소는 오류가 아닙니다");
        }

        [Test]
        public void GenerateRollbackScriptCommand_UsesThePreviousRevisionFromGit()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            _git.Setup(g => g.GetFileContentBeforeLastCommit(Server, Database, "dbo/Tables/Users.sql"))
                .Returns("CREATE TABLE Users (Id INT, OldCol INT);");
            var outputPath = Path.Combine(repoPath, "rollback.sql");
            _saveDialog.PathToReturn = outputPath;

            vm.GenerateRollbackScriptCommand.Execute(null);

            var script = File.ReadAllText(outputPath);
            Assert.That(script, Does.Contain("DBVC Rollback Script"));
            Assert.That(script, Does.Contain("OldCol"));
        }

        [Test]
        public void GenerateRollbackScriptCommand_WarnsAndSkipsSave_WhenNoObjectHasAPreviousRevision()
        {
            var vm = NewViewModelWithOneCheckedChange(out _);
            _git.Setup(g => g.GetFileContentBeforeLastCommit(Server, Database, It.IsAny<string>()))
                .Returns((string?)null);

            vm.GenerateRollbackScriptCommand.Execute(null);

            Assert.That(_saveDialog.CallCount, Is.EqualTo(0), "저장할 내용이 없으면 대화상자를 띄우지 않아야 합니다");
            Assert.That(vm.WarningMessage, Does.Contain("dbo.Users"));
        }

        [Test]
        public void GenerateDeploymentScriptCommand_ReportsExcludedObjectsAfterSaving()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            // 파일이 없는 두 번째 객체를 목록에 추가한다.
            vm.Changes.Add(new ChangeItemViewModel
            {
                ObjectName = "dbo.Gone",
                State = "Modified",
                RelativePath = "dbo/Tables/Gone.sql",
                IsSelected = true
            });
            _saveDialog.PathToReturn = Path.Combine(repoPath, "deploy.sql");

            vm.GenerateDeploymentScriptCommand.Execute(null);

            Assert.That(vm.WarningMessage, Does.Contain("dbo.Gone"));
        }

        [Test]
        public void GenerateScriptCommands_CannotExecute_WhenNothingIsChecked()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.GenerateDeploymentScriptCommand.CanExecute(null), Is.False);
            Assert.That(vm.GenerateRollbackScriptCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void GenerateScriptCommands_DoNotRequireACommitMessage()
        {
            var vm = NewViewModelWithOneCheckedChange(out _);
            vm.CommitMessage = null;

            Assert.That(vm.GenerateDeploymentScriptCommand.CanExecute(null), Is.True);
            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void GenerateDeploymentScriptCommand_Notifies_WhenWritingTheFileFails()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            // 디렉터리 경로를 파일 경로로 주면 쓰기가 실패한다.
            _saveDialog.PathToReturn = repoPath;

            Assert.DoesNotThrow(() => vm.GenerateDeploymentScriptCommand.Execute(null));

            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
        }

        private sealed class RecordingSaveDialog : IFileSaveDialog
        {
            public string? PathToReturn { get; set; }
            public int CallCount { get; private set; }
            public string? LastDefaultFileName { get; private set; }

            public string? PromptForSavePath(string title, string defaultFileName)
            {
                CallCount++;
                LastDefaultFileName = defaultFileName;
                return PathToReturn;
            }
        }

        private sealed class RecordingNotifier : IUserNotifier
        {
            public List<string> Errors { get; } = new List<string>();

            public void ShowError(string title, string message) => Errors.Add(message);
        }

        private sealed class RecordingFolderDialog : IFolderBrowseDialog
        {
            public string? PathToReturn { get; set; }
            public int CallCount { get; private set; }

            public string? PromptForFolder(string description, string? initialPath)
            {
                CallCount++;
                return PathToReturn;
            }
        }
    }
}
