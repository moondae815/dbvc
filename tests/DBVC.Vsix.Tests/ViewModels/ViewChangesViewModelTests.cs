using System;
using System.Threading;
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
        private RecordingConnectDialog _connectDialog = null!;
        private Mock<ISqlCredentialStore> _credentials = null!;
        private Mock<ISsmsConnectionSource> _ssms = null!;
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
            _connectDialog = new RecordingConnectDialog();
            _config = new Mock<IConfigManager>();
            _stateTracker = new Mock<IStateTracker>();
            _git = new Mock<IGitManager>();
            _smo = new Mock<ISmoManager>();
            _notifier = new RecordingNotifier();

            // 기본값: 매핑되어 있고 초기화되어 있으며 변경 없음
            _config.Setup(c => c.TryGetMapping(Server, Database))
                .Returns(new MappingConfig { ServerName = Server, DatabaseName = Database, GitPath = @"C:\repo" });
            _stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>())).Returns(StateTracker.RequiredSchemaVersion);
            // null = 접속 성공. 인증 실패 경로를 보는 테스트만 이 값을 덮어쓴다.
            _stateTracker.Setup(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
            _stateTracker.Setup(s => s.RefreshState(Server, Database, It.IsAny<bool>())).Returns(true);
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>());
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>())).Returns(new ScriptResult());
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>())).Returns(new List<string>());
            // 차단되지 않은 정상 저장소가 기본값이다. 차단 경로를 보는 테스트만 이 값을 덮어쓴다.
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState { CurrentBranch = "main", BlockReason = RepositoryBlockReason.None });
            // 기본은 "경고할 것 없음". 이 줄이 없으면 Moq가 null을 돌려주고 Commit이 죽는다.
            _stateTracker.Setup(s => s.GetCoAuthorWarnings(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(new CoAuthorWarning[0]);

            _cleaner = new Mock<IWorkingTreeCleaner>();
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(new CleanupResult());

            _git.Setup(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new List<CommitInfo>());
            _git.Setup(g => g.HasCommitsToPush(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            // 목을 쓰는 이유: 저장소에 무엇이 어떤 인자로 전달됐는지 Moq로 직접 검증하기 위해서다.
            _credentials = new Mock<ISqlCredentialStore>();

            // 기본값: SSMS 연결 없음 = 개체 탐색기에서 읽어 올 대상이 없다.
            _ssms = new Mock<ISsmsConnectionSource>();
            _ssms.Setup(s => s.TryGetCurrent()).Returns((SsmsConnectionInfo?)null);
        }

        private ViewChangesViewModel NewViewModel()
        {
            return new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _connectDialog, _credentials.Object, _ssms.Object);
        }

        /// <summary>
        /// 개체 탐색기가 Server/Database를 내주는 상태로 만들고 Connect를 누른다.
        /// 실제 앱에 남은 유일한 접속 경로다.
        /// </summary>
        private ViewChangesViewModel NewConnectedViewModel()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            var vm = NewViewModel();
            vm.ConnectCommand.Execute(null);
            return vm;
        }

        private static SsmsConnectionInfo Info(
            string server = Server,
            string database = Database,
            SqlAuthMode authMode = SqlAuthMode.Windows,
            string? userName = null,
            string? password = null,
            string? unsupportedReason = null)
            => new SsmsConnectionInfo(server, database, authMode, userName, password, unsupportedReason);

        private static ChangeRecord Record(string schema, string name, string state, string path, string objectType = "")
            => new ChangeRecord
            {
                Schema = schema,
                ObjectName = name,
                ObjectType = objectType,
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

            _stateTracker.Verify(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            Assert.That(vm.IsInitialized, Is.True);
        }

        [Test]
        public void SetContext_MarksNotInitialized_WhenTrackerSaysSo()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>())).Returns(0);

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsInitialized, Is.False, "설치되지 않은 DB에서는 Setup 오버레이가 보여야 합니다");
        }

        [Test]
        public void SetContext_ReportsUnmappedDatabase_WithAProminentWarning()
        {
            // 설계: "현재 데이터베이스에 연결된 Git 저장소가 없습니다." 경고 표시 + 커밋 비활성화
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsMapped, Is.False);
            Assert.That(vm.WarningMessage, Does.Contain("연결된 Git 저장소가 없습니다"));
        }

        [Test]
        public void SetContext_ClearsWarning_WhenDatabaseIsMapped()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.IsMapped, Is.True);
            Assert.That(vm.WarningMessage, Is.Null.Or.Empty);
        }

        [Test]
        public void VersionText_ShowsTheInstalledExtensionVersion()
        {
            Assert.That(NewViewModel().VersionText,
                Is.EqualTo("DBVC " + DBVC.Vsix.DbvcVersion.Current));
        }

        // ---------- 연결 ----------

        [Test]
        public void TargetSummary_SaysNotConnected_BeforeAnyConnect()
        {
            Assert.That(NewViewModel().TargetSummary, Is.EqualTo("(접속되지 않음)"));
        }

        [Test]
        public void TargetSummary_ShowsTheWindowsAuthTarget()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.TargetSummary, Is.EqualTo($"{Server}.{Database} — Windows 인증"));
        }

        [Test]
        public void TargetSummary_ShowsTheSqlAuthAccount()
        {
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(authMode: SqlAuthMode.Sql, userName: "sa", password: "p@ss"));

            var vm = NewViewModel();
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.TargetSummary, Is.EqualTo($"{Server}.{Database} — SQL 인증 (sa)"));
        }

        [Test]
        public void ConnectCommand_AdoptsTheObjectExplorerTarget()
        {
            var vm = NewConnectedViewModel();

            Assert.That(vm.ServerName, Is.EqualTo(Server));
            Assert.That(vm.DatabaseName, Is.EqualTo(Database));
            Assert.That(vm.IsMapped, Is.True);
            _stateTracker.Verify(s => s.GetInstalledVersion(Server, Database), Times.Once);
        }

        [Test]
        public void ConnectCommand_StoresTheCredentialFromObjectExplorer()
        {
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(authMode: SqlAuthMode.Sql, userName: "sa", password: "p@ss"));

            NewViewModel().ConnectCommand.Execute(null);

            _credentials.Verify(c => c.Set(Server, Database, SqlAuthMode.Sql, "sa", "p@ss"), Times.Once);
        }

        [Test]
        public void ConnectCommand_ReplacesTheStoredCredential_WhenTheTargetMoves()
        {
            // 한 대상에서 모은 인증 정보가 다른 대상으로 흘러가면 안 된다.
            // 예전에는 네 setter가 각각 ForgetSsmsPassword()로 막았고, 이제는 SetTarget이
            // 네 값을 통째로 갈아 끼우는 것으로 같은 보장을 한다.
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(authMode: SqlAuthMode.Sql, userName: "sa", password: "p@ss"));
            var vm = NewViewModel();
            vm.ConnectCommand.Execute(null);

            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(server: "OtherServer", database: "OtherDB"));
            vm.ConnectCommand.Execute(null);

            _credentials.Verify(c => c.Set("OtherServer", "OtherDB", SqlAuthMode.Windows, null, null), Times.Once);
            _credentials.Verify(
                c => c.Set("OtherServer", "OtherDB", SqlAuthMode.Sql, It.IsAny<string>(), It.IsAny<string>()),
                Times.Never,
                "이전 대상의 SQL 인증 정보가 새 대상으로 따라가면 안 됩니다");
            Assert.That(vm.AuthMode, Is.EqualTo(SqlAuthMode.Windows));
            Assert.That(vm.UserName, Is.Null);
        }

        [Test]
        public void ConnectCommand_CanExecute_OnlyWhenAConnectionSourceIsWired()
        {
            Assert.That(NewViewModel().ConnectCommand.CanExecute(null), Is.True);

            var withoutSource = new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _connectDialog, _credentials.Object, null);

            Assert.That(withoutSource.ConnectCommand.CanExecute(null), Is.False,
                "개체 탐색기를 읽을 수 없으면 누를 수 있는 것이 아무것도 없습니다");
        }

        [Test]
        public void ConnectCommand_ExplainsWhatToSelect_WhenTheSelectionCannotBeRead()
        {
            // 기본값: _ssms가 null을 돌려준다
            var vm = NewViewModel();

            vm.ConnectCommand.Execute(null);

            Assert.That(vm.WarningMessage, Does.Contain("개체 탐색기"));
            Assert.That(vm.ServerName, Is.Null);
            _stateTracker.Verify(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "대상을 모르는 채로 접속을 시도할 수는 없습니다");
        }

        [Test]
        public void ConnectCommand_KeepsTheCurrentTarget_WhenTheSelectionCannotBeRead()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns((SsmsConnectionInfo?)null);

            vm.ConnectCommand.Execute(null);

            Assert.That(vm.ServerName, Is.EqualTo(Server),
                "읽지 못했다는 사실이 이미 잡아 둔 대상을 거짓으로 만들지는 않습니다");
            Assert.That(vm.DatabaseName, Is.EqualTo(Database));
        }

        [Test]
        public void ConnectCommand_ShowsTheReason_AndDoesNotConnect_WhenTheConnectionIsUnsupported()
        {
            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(Info(unsupportedReason: "Microsoft Entra ID 연결은 그대로 재사용할 수 없습니다."));

            var vm = NewViewModel();
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.ServerName, Is.EqualTo(Server), "서버·DB는 알 수 있으므로 표시한다");
            Assert.That(vm.WarningMessage, Does.Contain("Entra"));
            _stateTracker.Verify(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "실패가 확정된 접속을 시도해 낮은 수준 오류를 흘리면 안 됩니다");
            _credentials.Verify(
                c => c.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SqlAuthMode>(),
                    It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void ConnectCommand_ShowsTheConnectionError_AndDoesNotClaimInitialized()
        {
            _stateTracker.Setup(s => s.TestConnection(Server, Database))
                .Returns("'LocalServer'에 로그인하지 못했습니다. 사용자명과 암호를 확인하세요.");

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsInitialized, Is.False);
            Assert.That(vm.WarningMessage, Does.Contain("로그인하지 못했습니다"),
                "접속 실패를 '초기화되지 않음'으로 뭉개면 원인을 알 수 없습니다");
            _stateTracker.Verify(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "접속도 안 되는 상태에서 초기화 여부를 물을 이유가 없습니다");
        }

        [Test]
        public void ConnectCommand_ClearsTheSelection_WhenTheTargetMoves()
        {
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };

            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.SelectedChange, Is.Null,
                "A의 변경 목록이 남아 있으면 커밋이 B로 나갑니다");
            Assert.That(vm.Changes, Is.Empty);
        }

        // ---------- 개체 탐색기 선택 대조 ----------

        [Test]
        public void CheckSsmsSelection_TellsWhatToSelect_WhenNothingIsConnectedAndNothingIsSelected()
        {
            var vm = NewViewModel();

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Does.Contain("선택"),
                "입력란이 없어졌으므로 이 한 줄이 유일한 길잡이입니다");
        }

        [Test]
        public void CheckSsmsSelection_PreviewsTheTarget_BeforeTheFirstConnect()
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            var vm = NewViewModel();

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Does.Contain(Server));
            Assert.That(vm.SsmsHintMessage, Does.Contain("— 연결을 누르세요"));
        }

        [Test]
        public void CheckSsmsSelection_PointsAtTheNewTarget_WhenTheSelectionMoved()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Does.Contain("OtherServer"));
            Assert.That(vm.HasSsmsHintMessage, Is.True);
        }

        [Test]
        public void CheckSsmsSelection_DoesNotTouchTheTarget()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));

            vm.CheckSsmsSelection();

            Assert.That(vm.ServerName, Is.EqualTo(Server),
                "지나가던 마우스가 대상을 바꾸면 버튼을 유지하기로 한 결정이 무의미해집니다");
        }

        [Test]
        public void CheckSsmsSelection_SaysNothing_WhenTheSelectionStillMatches()
        {
            var vm = NewConnectedViewModel();

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Is.Null);
        }

        [Test]
        public void CheckSsmsSelection_SaysNothing_WhenConnectedAndTheSelectionIsNotUsable()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns((SsmsConnectionInfo?)null);

            vm.CheckSsmsSelection();

            Assert.That(vm.SsmsHintMessage, Is.Null,
                "개체 탐색기에서 잠깐 다른 노드를 클릭했다고 배너가 뜨면 진짜 경고까지 묻힙니다");
        }

        [Test]
        public void ConnectCommand_ClearsTheHint_WhenItAdoptsTheSelection()
        {
            var vm = NewConnectedViewModel();
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info(server: "OtherServer", database: "OtherDB"));
            vm.CheckSsmsSelection();
            Assert.That(vm.HasSsmsHintMessage, Is.True);

            vm.ConnectCommand.Execute(null);

            Assert.That(vm.SsmsHintMessage, Is.Null,
                "방금 누른 버튼이 배너를 남긴 것처럼 보이면 안 됩니다");
        }

        // ---------- Setup ----------

        [Test]
        public void SetupCommand_InstallsTheChangeLogAndTrigger()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>())).Returns(0);
            var vm = NewConnectedViewModel();

            vm.SetupCommand.Execute(null);

            _stateTracker.Verify(s => s.InitializeDatabase(Server, Database), Times.Once);
            Assert.That(vm.IsInitialized, Is.True);
        }

        [Test]
        public void SetupCommand_RefreshesAfterSuccessfulInstall()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>())).Returns(0);
            var vm = NewConnectedViewModel();

            vm.SetupCommand.Execute(null);

            _stateTracker.Verify(s => s.RefreshState(Server, Database, It.IsAny<bool>()), Times.AtLeastOnce);
        }

        [Test]
        public void SetupCommand_KeepsOverlayVisibleAndNotifies_WhenInstallationFails()
        {
            // 권한 부족(db_owner 아님) 등으로 설치가 실패하면 초기화되었다고 주장해서는 안 된다.
            _stateTracker.Setup(s => s.GetInstalledVersion(It.IsAny<string>(), It.IsAny<string>())).Returns(0);
            _stateTracker.Setup(s => s.InitializeDatabase(It.IsAny<string>(), It.IsAny<string>()))
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
            var vm = NewViewModel(); // Connect 호출 안 함 — 대상이 없음

            vm.SetupCommand.Execute(null);

            _stateTracker.Verify(s => s.InitializeDatabase(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(vm.IsInitialized, Is.False);
        }

        // ---------- 추적기 버전 ----------

        [Test]
        public void IsTrackerOutdated_IsTrue_WhenTheInstalledVersionIsBehind()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(1);

            var vm = NewConnectedViewModel();

            Assert.Multiple(() =>
            {
                Assert.That(vm.IsTrackerOutdated, Is.True);
                Assert.That(vm.IsInitialized, Is.True, "구버전도 설치된 것이다 - 초기화 오버레이를 다시 띄우면 안 된다");
            });
        }

        [Test]
        public void IsTrackerOutdated_IsFalse_WhenTheTrackerIsCurrent()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(StateTracker.RequiredSchemaVersion);

            Assert.That(NewConnectedViewModel().IsTrackerOutdated, Is.False);
        }

        [Test]
        public void IsTrackerOutdated_IsFalse_WhenNothingIsInstalled()
        {
            // 미설치는 초기화 오버레이가 맡는다. 두 안내가 함께 뜨면 무엇을 눌러야 하는지 흐려진다.
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(0);

            Assert.That(NewConnectedViewModel().IsTrackerOutdated, Is.False);
        }

        [Test]
        public void UpdateTracker_ReinstallsTheScript_AndTellsTheUserToReExtract()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(1);
            var vm = NewConnectedViewModel();

            vm.UpdateTrackerCommand.Execute(null);

            _stateTracker.Verify(s => s.InitializeDatabase(Server, Database), Times.Once);
            Assert.That(_notifier.Infos.Any(m => m.Contains("전체 다시 추출")), Is.True,
                "과거 인덱스 변경이 정리되면서 사라지므로 다시 추출하라고 알려야 한다");
        }

        [Test]
        public void UpdateTracker_ShowsTheReason_WhenInstallFails()
        {
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(1);
            _stateTracker.Setup(s => s.InitializeDatabase(Server, Database))
                .Throws(new InvalidOperationException("권한이 없습니다"));
            var vm = NewConnectedViewModel();

            vm.UpdateTrackerCommand.Execute(null);

            Assert.That(_notifier.Errors.Any(m => m.Contains("권한이 없습니다")), Is.True);
        }

        [Test]
        public void Setup_RunsThroughTheScheduler()
        {
            // 설치는 응답 없는 서버에서 수십 초까지 걸린다. UI 스레드에 남으면 그동안 SSMS가 멈춘다.
            var scheduler = new CountingScheduler();
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(0);
            var vm = new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _connectDialog, _credentials.Object, _ssms.Object, scheduler);
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            vm.ConnectCommand.Execute(null);
            var before = scheduler.RunCount;

            vm.SetupCommand.Execute(null);

            Assert.That(scheduler.RunCount, Is.GreaterThan(before));
        }

        [Test]
        public void InstallSchema_RaisesIsBusy_WhileTheScriptRuns()
        {
            // IsBusy가 서지 않으면 설치가 도는 동안 새로고침·커밋 버튼이 함께 눌린다.
            // CountingScheduler가 작업을 인라인으로 돌려주므로 작업 콜백 안에서 그 값을 볼 수 있다.
            var scheduler = new CountingScheduler();
            _stateTracker.Setup(s => s.GetInstalledVersion(Server, Database)).Returns(0);
            var vm = new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _connectDialog, _credentials.Object, _ssms.Object, scheduler);
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            vm.ConnectCommand.Execute(null);

            bool? busyDuringInstall = null;
            _stateTracker.Setup(s => s.InitializeDatabase(Server, Database))
                .Callback(() => busyDuringInstall = vm.IsBusy);

            vm.SetupCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(busyDuringInstall, Is.True, "설치가 도는 동안 IsBusy가 서 있어야 한다");
                Assert.That(vm.IsBusy, Is.False, "끝나면 다시 내려놓아야 한다");
            });
        }

        /// <summary>넘겨받은 작업을 인라인으로 실행하되 횟수를 센다.</summary>
        private sealed class CountingScheduler : IBackgroundScheduler
        {
            public int RunCount { get; private set; }

            public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
            {
                RunCount++;
                T value;
                try { value = work(); }
                catch (Exception ex) { onFailed(ex); return; }
                onSucceeded(value);
            }

            public void Post(Action action) => action();
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
        public void RefreshCommand_PopulatesObjectTypeOnChanges()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "usp_GetUser", "Modified", "dbo/Stored Procedures/usp_GetUser.sql", "PROCEDURE"),
                Record("dbo", "fn_GetTotal", "Added", "dbo/Functions/fn_GetTotal.sql", "FUNCTION"),
                Record("dbo", "Orders", "Modified", "dbo/Tables/Orders.sql", "TABLE"),
                Record("dbo", "CustomSynonym", "Added", "dbo/Synonyms/CustomSynonym.sql", "SYNONYM")
            });
            var vm = NewConnectedViewModel();

            vm.RefreshCommand.Execute(null);

            Assert.That(vm.Changes[0].ObjectType, Is.EqualTo("PROCEDURE"));
            Assert.That(vm.Changes[0].ObjectTypeText, Is.EqualTo("SP"));
            Assert.That(vm.Changes[1].ObjectType, Is.EqualTo("FUNCTION"));
            Assert.That(vm.Changes[1].ObjectTypeText, Is.EqualTo("UDF"));
            Assert.That(vm.Changes[2].ObjectType, Is.EqualTo("TABLE"));
            Assert.That(vm.Changes[2].ObjectTypeText, Is.EqualTo("Table"));
            Assert.That(vm.Changes[3].ObjectType, Is.EqualTo("SYNONYM"));
            Assert.That(vm.Changes[3].ObjectTypeText, Is.EqualTo("Synonym"));
        }

        [Test]
        public void RefreshCommand_ExportsObjectsWithSmoBeforeReadingState()
        {
            // Diff와 커밋이 최신 DB 코드를 보려면 새로고침 시 스크립트 추출이 선행되어야 한다.
            var sequence = new List<string>();
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Callback(() => sequence.Add("script"))
                .Returns(new ScriptResult());
            _stateTracker.Setup(s => s.RefreshState(Server, Database, It.IsAny<bool>()))
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

            _smo.Verify(s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(vm.WarningMessage, Does.Contain("연결된 Git 저장소가 없습니다"));
        }

        [Test]
        public void RefreshCommand_ReportsPartialScriptingFailures()
        {
            var result = new ScriptResult { SucceededCount = 3 };
            result.FailedObjects.Add("dbo.Broken");
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>())).Returns(result);
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

        // ---------- 정리 실패한 삭제 객체는 커밋에서 제외 (조용한 소실 방지) ----------

        [Test]
        public void Refresh_DeselectsTheChangeItem_WhenItsWorkingTreeCleanupFailed()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Deleted", "dbo/Tables/Users.sql")
            });
            var failed = new CleanupResult();
            failed.FailedPaths.Add("dbo/Tables/Users.sql");
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(failed);

            var vm = NewConnectedViewModel();

            Assert.That(vm.Changes.Single().IsSelected, Is.False,
                "정리에 실패한 항목을 체크된 채로 두면 파일이 남아 있는데도 삭제가 커밋된 것처럼 보일 수 있습니다");
        }

        [Test]
        public void Commit_ExcludesTheFailedCleanupObject_FromGitManager_EvenWhenReChecked()
        {
            // 사용자가 경고 배너를 무시하고 체크박스를 다시 켠 경우를 재현한다.
            // 체크박스만으로는 부족하므로 Commit이 한 번 더 걸러내야 한다.
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Deleted", "dbo/Tables/Users.sql"),
                Record("dbo", "Orders", "Modified", "dbo/Tables/Orders.sql")
            });
            var failed = new CleanupResult();
            failed.FailedPaths.Add("dbo/Tables/Users.sql");
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(failed);
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(GitCommitResult.Committed);

            var vm = NewConnectedViewModel();
            vm.Changes.Single(c => c.ObjectName == "dbo.Users").IsSelected = true; // 다시 체크
            vm.Changes.Single(c => c.ObjectName == "dbo.Orders").IsSelected = true;
            vm.CommitMessage = "Drop Users, modify Orders";

            vm.CommitCommand.Execute(null);

            _git.Verify(g => g.CommitChanges(Server, Database, "Drop Users, modify Orders",
                It.Is<IEnumerable<string>>(paths => paths.SequenceEqual(new[] { "dbo/Tables/Orders.sql" }))), Times.Once,
                "정리에 실패한 삭제 객체의 경로가 Git에 넘어가면 삭제되지 않은 파일이 커밋된 것처럼 보입니다");
        }

        [Test]
        public void Commit_DoesNotMarkTheFailedCleanupObjectProcessed_EvenWhenAnotherObjectCommitsSuccessfully()
        {
            // 조용한 소실 시나리오: 정리 실패한 삭제 객체와 함께 다른 객체를 커밋하면
            // CommitChanges는 true를 반환한다. 이 true를 근거로 실패한 객체까지
            // MarkProcessed에 넘기면 DDL 로그 행이 처리 완료로 표시되어 다음 새로고침에서
            // 파일은 그대로인데 목록에서만 사라진다.
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Deleted", "dbo/Tables/Users.sql"),
                Record("dbo", "Orders", "Modified", "dbo/Tables/Orders.sql")
            });
            var failed = new CleanupResult();
            failed.FailedPaths.Add("dbo/Tables/Users.sql");
            _cleaner.Setup(c => c.RemoveDeletedObjectFiles(It.IsAny<string>(), It.IsAny<IEnumerable<ChangeRecord>>()))
                .Returns(failed);
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(GitCommitResult.Committed);

            var vm = NewConnectedViewModel();
            vm.Changes.Single(c => c.ObjectName == "dbo.Users").IsSelected = true; // 다시 체크
            vm.Changes.Single(c => c.ObjectName == "dbo.Orders").IsSelected = true;
            vm.CommitMessage = "Drop Users, modify Orders";

            vm.CommitCommand.Execute(null);

            _stateTracker.Verify(s => s.MarkProcessed(Server, Database,
                It.Is<IEnumerable<ChangeRecord>>(records =>
                    records.All(r => r.QualifiedName != "dbo.Users") &&
                    records.Any(r => r.QualifiedName == "dbo.Orders"))),
                Times.Once,
                "정리에 실패한 삭제가 처리 완료로 표시되면 파일이 남아 있는데도 다음 새로고침에서 조용히 사라집니다");
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
            _connectDialog.RequestToReturn = RepositoryConnectRequest.ForExistingFolder(@"C:\chosen-repo");
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(Server, Database, @"C:\chosen-repo"), Times.Once);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNothing_WhenTheUserCancels()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn = null;
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(_connectDialog.CallCount, Is.EqualTo(1));
            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Empty, "취소는 오류가 아닙니다");
        }

        [Test]
        public void ConnectRepositoryCommand_RefusesAFolderThatIsNotAGitRepository()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _git.Setup(g => g.IsRepository(It.IsAny<string>())).Returns(false);
            _connectDialog.RequestToReturn = RepositoryConnectRequest.ForExistingFolder(@"C:\not-a-repo");
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Not.Empty,
                "유효하지 않은 경로를 저장하면 이후 모든 동작이 조용히 실패합니다");
        }

        [Test]
        public void ConnectRepositoryCommand_ClonesAndSavesTheMapping_WhenTheUserChoosesToClone()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");
            _git.Setup(g => g.CloneRepository(
                    "git@host:org/db-schema.git", @"C:\repos\db-schema",
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(@"C:\repos\db-schema");
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(Server, Database, @"C:\repos\db-schema"), Times.Once);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNotSaveTheMapping_WhenCloneFails()
        {
            // 절반만 받아진 저장소가 매핑되면 이후 모든 동작이 조용히 이상해진다.
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");
            _git.Setup(g => g.CloneRepository(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Throws(new GitRemoteException("원격과 통신하지 못했습니다."));
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Not.Empty);
        }

        [Test]
        public void ConnectRepositoryCommand_DoesNotReportCancellationAsAnError_WhenTheUserCancelsTheClone()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");
            _git.Setup(g => g.CloneRepository(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Throws(new OperationCanceledException("원격 저장소 받기를 취소했습니다."));
            var vm = NewConnectedViewModel();

            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty,
                "사용자가 누른 취소를 오류 상자로 알리면 자기가 누른 것을 오류로 되읽습니다");
            _config.Verify(c => c.AddMapping(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void ConnectRepositoryCommand_StopsOfferingCancel_WhenTheCloneReachesCheckout()
        {
            // libgit2의 checkout 콜백은 중단을 받지 않는다. 눌러도 안 멈추는 버튼을
            // 살려 두면 사용자가 도구가 굳었다고 읽는다.
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");

            ViewChangesViewModel? vm = null;
            var cancellableDuringCheckout = true;

            _git.Setup(g => g.CloneRepository(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Returns((string url, string path, IProgress<CloneProgress>? progress, CancellationToken token) =>
                {
                    progress?.Report(new CloneProgress(ClonePhase.Transferring, 10, 100));
                    progress?.Report(new CloneProgress(ClonePhase.CheckingOut, 1, 10));
                    cancellableDuringCheckout = vm!.CancelCommand.CanExecute(null);
                    return path;
                });

            vm = NewConnectedViewModel();
            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(cancellableDuringCheckout, Is.False);
        }

        [Test]
        public void ConnectRepositoryCommand_IsDisabled_WhileAnotherOperationIsRunning()
        {
            // IsBusy 가드가 없으면 진행 중에 두 번째 클릭이 CloneAndConnect를 다시 태우고,
            // 그 첫 줄이 첫 clone이 아직 쓰는 CancellationTokenSource를 Dispose한다 -
            // 두 clone이 동시에 돌고 취소가 더 이상 첫 clone을 멈추지 못하게 된다.
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);
            _connectDialog.RequestToReturn =
                RepositoryConnectRequest.ForClone("git@host:org/db-schema.git", @"C:\repos\db-schema");

            ViewChangesViewModel? vm = null;
            var canExecuteDuringClone = true;

            _git.Setup(g => g.CloneRepository(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IProgress<CloneProgress>>(), It.IsAny<CancellationToken>()))
                .Returns((string url, string path, IProgress<CloneProgress>? progress, CancellationToken token) =>
                {
                    canExecuteDuringClone = vm!.ConnectRepositoryCommand.CanExecute(null);
                    return path;
                });

            vm = NewConnectedViewModel();
            vm.ConnectRepositoryCommand.Execute(null);

            Assert.That(canExecuteDuringClone, Is.False);
        }

        // ---------- 객체 이력 ----------

        [Test]
        public void SelectedChange_LoadsTheHistoryOfTheSelectedObject()
        {
            _git.Setup(g => g.GetHistory(Server, Database, "dbo/Tables/Users.sql"))
                .Returns(new List<CommitInfo>
                {
                    new CommitInfo { Sha = "a3f9c2b1d4", Message = "인덱스 추가", Author = "Tester", Date = DateTimeOffset.Now }
                });
            var vm = NewConnectedViewModel();

            vm.SelectedChange = new ChangeItemViewModel
            {
                ObjectName = "dbo.Users",
                RelativePath = "dbo/Tables/Users.sql"
            };

            Assert.That(vm.History.Entries, Has.Count.EqualTo(1));
            Assert.That(vm.History.Entries[0].ShortSha, Is.EqualTo("a3f9c2b"));
        }

        [Test]
        public void SelectedChange_FallsBackToTheRepositoryHistory_WhenTheSelectionIsCleared()
        {
            _git.Setup(g => g.GetHistory(Server, Database, "dbo/Tables/Users.sql"))
                .Returns(new List<CommitInfo>
                {
                    new CommitInfo { Sha = "a3f9c2b1d4", Message = "인덱스 추가", Author = "Tester", Date = DateTimeOffset.Now }
                });
            _git.Setup(g => g.GetHistory(Server, Database, It.Is<string?>(p => string.IsNullOrWhiteSpace(p))))
                .Returns(new List<CommitInfo>
                {
                    new CommitInfo { Sha = "bbb2222333", Message = "저장소 커밋", Author = "Tester", Date = DateTimeOffset.Now }
                });
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };

            vm.SelectedChange = null;

            Assert.That(vm.History.Entries.Select(e => e.ShortSha), Is.EqualTo(new[] { "bbb2222" }),
                "선택이 풀리면 이력이 사라지는 것이 아니라 저장소 전체로 넓어져야 합니다");
            Assert.That(vm.History.ScopeLabel, Is.EqualTo("저장소 전체"));
        }

        [Test]
        public void Refresh_ClearsTheSelection()
        {
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };

            vm.Refresh();

            Assert.That(vm.SelectedChange, Is.Null,
                "목록을 비웠는데 선택이 남으면 Diff와 이력이 목록에 없는 객체를 가리킵니다");
        }

        // ---------- Pull ----------

        [Test]
        public void PullCommand_IsEnabled_WhenTheDatabaseIsMapped()
        {
            Assert.That(NewConnectedViewModel().PullCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void PullCommand_IsDisabled_WhenTheDatabaseIsNotMapped()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);

            Assert.That(NewConnectedViewModel().PullCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void PullCommand_PullsWithoutAsking_WhenTheWorkingTreeIsClean()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.Zero, "잃을 것이 없으면 묻지 않습니다");
            _git.Verify(g => g.PullChanges(Server, Database), Times.Once);
        }

        [Test]
        public void PullCommand_AsksForConfirmation_WhenUncommittedChangesExist()
        {
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>()))
                .Returns(new List<string> { "dbo/Tables/Users.sql", "dbo/Views/vw_Sales.sql" });
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.EqualTo(1),
                "충돌 시 hard reset으로 미커밋 변경이 사라지므로 먼저 알려야 합니다");
            _git.Verify(g => g.PullChanges(Server, Database), Times.Once);
        }

        [Test]
        public void PullCommand_ReportsAMissingMapping_WhenPullChangesReturnsNoMapping()
        {
            // PullChanges가 NoMapping을 돌려주는 경우: GitManager 안에서 매핑을 다시 찾지 못한 경우다.
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.NoMapping);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Does.Contain("매핑된 Git 저장소를 찾을 수 없습니다"));
            Assert.That(_notifier.Infos, Is.Empty, "실패했는데 성공 알림이 뜨면 안 됩니다");
        }

        [Test]
        public void PullCommand_DoesNotPull_WhenTheUserCancelsTheConfirmation()
        {
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>()))
                .Returns(new List<string> { "dbo/Tables/Users.sql" });
            _notifier.ConfirmResult = false;
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.EqualTo(1), "물어봤는데 거절한 것과 아예 안 물어본 것을 구분해야 합니다");
            _git.Verify(g => g.PullChanges(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(_notifier.Errors, Is.Empty, "취소는 오류가 아닙니다");
        }

        [Test]
        public void PullCommand_ReportsAMergeConflict()
        {
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new MergeConflictException("병합 충돌이 발생하여 Pull을 중단했습니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Does.Contain("충돌"));
            Assert.That(_notifier.ErrorCalls[0].Title, Does.Contain("중단"),
                "병합 충돌 분기는 '실패'가 아니라 '중단' 타이틀을 써야 합니다 - 이 분기가 삭제되면 실패해야 합니다");
            Assert.That(_notifier.Infos, Is.Empty,
                "병합 충돌로 중단됐는데 성공 알림까지 뜨면 안 됩니다 - catch 끝의 return이 지워지면 실패해야 합니다");
        }

        [Test]
        public void PullCommand_ReportsAnUnexpectedFailure()
        {
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new InvalidOperationException("원격(remote)이 설정되어 있지 않습니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Errors, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors[0], Is.EqualTo("원격(remote)이 설정되어 있지 않습니다."),
                "원인이 타입으로 갈렸으므로 무관한 오류에 미커밋 변경 힌트를 덧붙이면 안 됩니다. 원문만 그대로 보여줍니다");
            Assert.That(_notifier.Infos, Is.Empty,
                "예기치 못한 실패인데 성공 알림까지 뜨면 안 됩니다 - catch 끝의 return이 지워지면 실패해야 합니다");
        }

        [Test]
        public void PullCommand_ReportsARejectedCheckout_WithoutClaimingAnythingWasLost()
        {
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new WorkingTreeConflictException(
                    "겹치는 미커밋 변경이 있어 Pull하지 않았습니다. 저장소는 변경되지 않았습니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Is.EqualTo("DBVC Pull 중단"),
                "아무 일도 일어나지 않았으므로 '실패'가 아니라 '중단'입니다");
            Assert.That(_notifier.ErrorCalls[0].Message, Does.Contain("변경되지 않았습니다"));
            Assert.That(_notifier.Infos, Is.Empty);
        }

        [Test]
        public void PullCommand_ReportsAnAuthenticationFailure_WithTheExceptionsOwnMessageIntact()
        {
            // GitAuthenticationException 전용 catch는 없다 - Core가 이미 완전한 한국어 안내를
            // 메시지에 담아 던지므로, 전용 분기를 두면 catch-all과 완전히 같은 동작
            // (제목 "DBVC Pull 실패" + ex.Message 그대로)을 중복할 뿐이다. 이 테스트는 그
            // catch-all 경로로 사용자에게 도달하는 결과를 고정한다. 되살리지 말 것.
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new GitAuthenticationException("원격이 사용자 자격 증명을 요구합니다."));
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Is.EqualTo("DBVC Pull 실패"));
            Assert.That(_notifier.ErrorCalls[0].Message, Is.EqualTo("원격이 사용자 자격 증명을 요구합니다."));
            Assert.That(_notifier.Infos, Is.Empty);
        }

        [Test]
        public void PullCommand_TellsTheUserThatARejectedPullLosesNothing_BeforeAsking()
        {
            _git.Setup(g => g.GetChangedFiles(It.IsAny<string>()))
                .Returns(new List<string> { "dbo/Tables/Users.sql" });
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ConfirmCalls[0].Message, Does.Contain("저장소는 그대로입니다"),
                "거부 경로는 무손실입니다. 두 결과를 뭉뚱그리면 사용자가 필요 이상으로 겁먹습니다");
            Assert.That(_notifier.ConfirmCalls[0].Message, Does.Contain("사라질 수 있습니다"),
                "충돌 경로의 손실 가능성은 여전히 알려야 합니다");
        }

        [Test]
        public void PullCommand_NotifiesOnSuccess_AndSaysWhereTheScriptsLanded()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Infos, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors, Is.Empty);
            Assert.That(_notifier.InfoCalls[0].Title, Is.EqualTo("DBVC Pull"),
                "제목이 실패와 구분되지 않으면 사용자가 목록만 보고도 결과를 알 수 없습니다");
            Assert.That(_notifier.Infos[0], Does.Contain(@"C:\repo"),
                "받은 스크립트가 어디 놓였는지 말하지 않으면 사용자가 찾지 못합니다");
        }

        [Test]
        public void PullCommand_ReportsAlreadyUpToDate_WhenNothingWasPulled()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            var vm = NewConnectedViewModel();

            vm.PullCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty, "받을 것이 없는 것은 오류가 아닙니다");
            Assert.That(_notifier.Infos, Has.Count.EqualTo(1));
            Assert.That(_notifier.InfoCalls[0].Title, Is.EqualTo("DBVC Pull"),
                "이미 최신인 경우도 성공 경로이므로 제목이 실패 알림과 섞이면 안 됩니다");
            Assert.That(_notifier.Infos[0], Does.Not.Contain("가져왔습니다"),
                "받은 것이 없는데 가져왔다고 말하면 사용자가 없는 스크립트를 찾아 헤맵니다");
            Assert.That(_notifier.Infos[0], Does.Contain("이미 최신"));
        }

        [Test]
        public void PullCommand_DoesNotReloadHistory_WhenNothingWasPulled()
        {
            // 이력을 다시 읽어도 내용은 같다. 그런데 화면이 다시 그려지면 사용자는
            // 무언가 받아왔다고 읽는다 - 안내 문구를 고친 이유와 같은 문제다.
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };
            // SelectedChange 대입 자체가 이력을 한 번 읽는다. 그것을 세지 않도록 지운다.
            _git.Invocations.Clear();
            int selectionChangedCount = 0;
            vm.SelectionChanged += (_, __) => selectionChangedCount++;

            vm.PullCommand.Execute(null);

            _git.Verify(g => g.GetHistory(Server, Database, It.IsAny<string?>()), Times.Never,
                "받은 것이 없으면 이력이 바뀌지 않았으므로 다시 읽을 이유가 없습니다");
            Assert.That(selectionChangedCount, Is.EqualTo(0),
                "Diff도 다시 렌더링할 이유가 없습니다");
        }

        [Test]
        public void PullCommand_ReloadsHistoryAndRendersDiff_AfterASuccessfulPull()
        {
            // Pull의 목적 자체가 새 커밋을 받는 것이므로, 성공 직후에는
            // History 탭과 Diff 탭이 Pull 이전 HEAD가 아니라 새 HEAD를 보여줘야 한다.
            var beforePull = new List<CommitInfo>
            {
                new CommitInfo { Sha = "aaa1111111", Message = "이전 커밋", Author = "Tester", Date = DateTimeOffset.Now }
            };
            var afterPull = new List<CommitInfo>
            {
                new CommitInfo { Sha = "bbb2222222", Message = "새 커밋", Author = "Tester", Date = DateTimeOffset.Now },
                new CommitInfo { Sha = "aaa1111111", Message = "이전 커밋", Author = "Tester", Date = DateTimeOffset.Now }
            };
            _git.SetupSequence(g => g.GetHistory(Server, Database, "dbo/Tables/Users.sql"))
                .Returns(beforePull)
                .Returns(afterPull);
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();
            vm.SelectedChange = new ChangeItemViewModel { ObjectName = "dbo.Users", RelativePath = "dbo/Tables/Users.sql" };
            Assert.That(vm.History.Entries.Select(e => e.ShortSha), Is.EqualTo(new[] { "aaa1111" }), "선행 조건: Pull 이전 이력");
            int selectionChangedCount = 0;
            vm.SelectionChanged += (_, __) => selectionChangedCount++;

            vm.PullCommand.Execute(null);

            Assert.That(vm.History.Entries.Select(e => e.ShortSha), Is.EqualTo(new[] { "bbb2222", "aaa1111" }),
                "Pull 성공 후 History 탭이 새 커밋을 반영해야 합니다");
            Assert.That(selectionChangedCount, Is.EqualTo(1),
                "Pull 성공 후 Diff 탭이 새 HEAD로 다시 렌더링되어야 합니다");
        }

        [Test]
        public void PullCommand_DoesNotRefresh_AfterASuccessfulPull()
        {
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();

            vm.PullCommand.Execute(null);

            _git.Verify(g => g.PullChanges(Server, Database), Times.Once, "Pull이 실제로 성공했다는 전제 자체를 확인해야 합니다");
            _smo.Verify(
                s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>?>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "Pull 직후 Refresh하면 방금 받은 원격 변경이 SMO 추출로 즉시 덮어써집니다");
        }

        [Test]
        public void PullCommand_RaisesPushCanExecuteChanged_AfterASuccessfulPull()
        {
            // 병합 커밋이 만들어지는 Pull은 올릴 커밋을 새로 만든다. 그런데 RelayCommand는
            // CommandManager.RequerySuggested를 구독하지 않으므로, 이 이벤트를 직접 올리지
            // 않으면 Push 버튼은 꺼진 채로 남는다 - 사용자가 새로고침이나 커밋 같은 다른
            // 동작을 할 때까지 올릴 수 있다는 사실이 화면에 드러나지 않는다.
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();
            int pushCanExecuteChangedCount = 0;
            vm.PushCommand.CanExecuteChanged += (_, __) => pushCanExecuteChangedCount++;

            vm.PullCommand.Execute(null);

            Assert.That(pushCanExecuteChangedCount, Is.GreaterThan(0),
                "Pull이 커밋을 만들어도 이벤트가 오지 않으면 Push 버튼은 눌리지 않는 상태로 남습니다");
        }

        // ---------- Push ----------

        [Test]
        public void PushCommand_IsEnabled_WhenTheDatabaseIsMapped()
        {
            Assert.That(NewConnectedViewModel().PushCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void PushCommand_IsDisabled_WhenTheDatabaseIsNotMapped()
        {
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns((MappingConfig?)null);

            Assert.That(NewConnectedViewModel().PushCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void PushCommand_CanExecute_OnlyWhenHasCommitsToPushIsTrue()
        {
            var vm = NewConnectedViewModel();

            // 처음에는 앞선 커밋이 없다고 가정
            _git.Setup(g => g.HasCommitsToPush(Server, Database)).Returns(false);
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.PushCommand.CanExecute(null), Is.False, "커밋이 없으므로 비활성화되어야 함");

            _git.Setup(g => g.HasCommitsToPush(Server, Database)).Returns(true);
            vm.RefreshCommand.Execute(null);

            Assert.That(vm.PushCommand.CanExecute(null), Is.True, "앞선 커밋이 생기면 활성화되어야 함");
        }

        [Test]
        public void PushCommand_PushesWithoutAsking()
        {
            // Push는 로컬 저장소도 작업 트리도 건드리지 않는다. Pull의 사전 확인은
            // 병합이 미커밋 변경을 지울 수 있어서인데, 여기엔 그 위험이 없다.
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.Pushed);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.Zero);
            _git.Verify(g => g.PushChanges(Server, Database), Times.Once);
        }

        [Test]
        public void PushCommand_NotifiesOnSuccess()
        {
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.Pushed);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.Infos, Has.Count.EqualTo(1));
            Assert.That(_notifier.Errors, Is.Empty);
        }

        [Test]
        public void PushCommand_ReportsNothingToPushAsInformation_NotAnError()
        {
            // 원격이 이미 최신인 것은 정상 상태다. 오류 대화상자를 띄우면 사용자가
            // 무언가 잘못됐다고 읽는다.
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.NothingToPush);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty);
            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("올릴 커밋이 없습니다"));
        }

        [Test]
        public void PushCommand_ReportsAMissingMapping()
        {
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.NoMapping);
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Is.EqualTo("DBVC Push 실패"));
        }

        [Test]
        public void PushCommand_ReportsARejection_WithTheExceptionsOwnMessageIntact()
        {
            // Core가 완전한 한국어 안내를 메시지에 담아 던진다. 전용 catch를 두면
            // catch-all과 글자 그대로 같은 코드가 된다 - Pull이 GitAuthenticationException에서
            // 실제로 겪고 제거한 결함이다. 이 테스트는 그 문구가 그대로 나오는지만 지킨다.
            _git.Setup(g => g.PushChanges(Server, Database))
                .Throws(new GitPushRejectedException("원격이 Push를 거부했습니다. Pull을 먼저 하세요."));
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Title, Is.EqualTo("DBVC Push 실패"));
            Assert.That(_notifier.ErrorCalls[0].Message, Is.EqualTo("원격이 Push를 거부했습니다. Pull을 먼저 하세요."));
        }

        [Test]
        public void PushCommand_ReportsAnUnexpectedFailure()
        {
            _git.Setup(g => g.PushChanges(Server, Database))
                .Throws(new InvalidOperationException("추적 중인 원격 브랜치가 없어 Push할 수 없습니다."));
            var vm = NewConnectedViewModel();

            vm.PushCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.ErrorCalls[0].Message, Does.Contain("추적"));
        }

        [Test]
        public void PushCommand_DoesNotRefresh_AfterASuccessfulPush()
        {
            // Push는 로컬에 아무것도 바꾸지 않는다. Refresh는 SMO 추출을 부르는 비싼 연산이며
            // 여기서 부를 이유가 없다.
            _git.Setup(g => g.PushChanges(Server, Database)).Returns(PushResult.Pushed);
            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();

            vm.PushCommand.Execute(null);

            _git.Verify(g => g.PushChanges(Server, Database), Times.Once, "Push가 실제로 성공했다는 전제 자체를 확인해야 합니다");
            _smo.Verify(
                s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>?>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ---------- 원격 확인 ----------

        [Test]
        public void CheckRemoteCommand_ShowsAheadAndBehindCounts_WhenTheRemoteAnswers()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 1));
            var vm = NewConnectedViewModel();

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(vm.RemoteStatusText, Does.Contain("받을 커밋 1개"));
            Assert.That(vm.RemoteStatusText, Does.Contain("올릴 커밋 2개"));
            Assert.That(vm.HasRemoteStatus, Is.True);
        }

        [Test]
        public void RemoteStatusText_IsEmpty_BeforeTheUserAsks()
        {
            // 누르기 전에는 아무것도 뜨지 않는다. 낡은 숫자를 최신인 척 보여주지 않기 위해서다.
            var vm = NewConnectedViewModel();

            Assert.That(vm.HasRemoteStatus, Is.False);
            _git.Verify(g => g.FetchRemoteStatus(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void RemoteStatusText_IsCleared_WhenTheTargetChanges()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(2, 1));
            var vm = NewConnectedViewModel();
            vm.CheckRemoteCommand.Execute(null);

            _ssms.Setup(s => s.TryGetCurrent())
                .Returns(new SsmsConnectionInfo("S2", "D2", SqlAuthMode.Windows, null, null, null));
            vm.ConnectCommand.Execute(null);

            Assert.That(vm.HasRemoteStatus, Is.False,
                "다른 대상의 원격 상태가 남으면 사용자가 엉뚱한 저장소의 숫자를 읽습니다");
        }

        [Test]
        public void RemoteStatusText_IsCleared_AfterASuccessfulPull()
        {
            // Pull이 뒤처짐을 줄이므로 원격 확인이 보여준 숫자는 낡는다. 지우지 않으면
            // 다 받은 뒤에도 "받을 커밋 n개"가 최신인 척 남는다.
            _git.Setup(g => g.FetchRemoteStatus(Server, Database)).Returns(new RemoteStatus(0, 3));
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.Pulled);
            var vm = NewConnectedViewModel();
            vm.CheckRemoteCommand.Execute(null);
            Assert.That(vm.HasRemoteStatus, Is.True, "선행 조건: 원격 확인으로 값을 채워 둔다");

            vm.PullCommand.Execute(null);

            Assert.That(vm.HasRemoteStatus, Is.False);
        }

        [Test]
        public void CheckRemoteCommand_ReportsTheReason_WhenTheRemoteCannotBeReached()
        {
            _git.Setup(g => g.FetchRemoteStatus(Server, Database))
                .Throws(new GitRemoteException("원격과 통신하지 못했습니다."));
            var vm = NewConnectedViewModel();

            vm.CheckRemoteCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Not.Empty);
            Assert.That(vm.HasRemoteStatus, Is.False);
        }

        [Test]
        public void CheckRemoteCommand_IsDisabled_WhenTheRepositoryIsBlocked()
        {
            // 차단은 경고가 아니다. 기준이 어긋난 저장소에서 낸 숫자는 조용히 거짓말이다.
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState
                {
                    CurrentBranch = "develop",
                    BlockReason = RepositoryBlockReason.BranchMismatch,
                    BlockMessage = "고정된 브랜치와 다릅니다."
                });
            var vm = NewConnectedViewModel();

            Assert.That(vm.CheckRemoteCommand.CanExecute(null), Is.False);
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
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(GitCommitResult.Committed);
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
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(GitCommitResult.Committed);
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            vm.Changes[0].IsSelected = true;
            vm.CommitMessage = "msg";

            vm.CommitCommand.Execute(null);

            _stateTracker.Verify(s => s.MarkProcessed(Server, Database, It.IsAny<IEnumerable<ChangeRecord>>()), Times.Once);
            Assert.That(vm.CommitMessage, Is.Empty, "커밋 성공 후 메시지 입력창은 비워져야 합니다");
        }

        /// <summary>
        /// 커밋하면 변경 목록이 비고, 목록이 비면 선택할 객체가 없다. 그래도 방금 만든 커밋은
        /// 이력 탭에 보여야 한다 — 첫 커밋 후 "아무 표시도 없다"고 보고된 결함이다.
        /// </summary>
        [Test]
        public void CommitCommand_ShowsTheNewCommitInTheHistory_EvenWithNoObjectSelected()
        {
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql")
            });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(GitCommitResult.Committed);
            _git.SetupSequence(g => g.GetHistory(Server, Database, It.Is<string?>(p => string.IsNullOrWhiteSpace(p))))
                .Returns(new List<CommitInfo>())
                .Returns(new List<CommitInfo>())
                .Returns(new List<CommitInfo>
                {
                    new CommitInfo { Sha = "a3f9c2b1d4", Message = "초기 스키마 스냅샷", Author = "Tester", Date = DateTimeOffset.Now }
                });
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            vm.Changes[0].IsSelected = true;
            vm.CommitMessage = "초기 스키마 스냅샷";

            // 사용자가 목록에서 객체를 선택한 적이 없다 — SelectedChange는 계속 null이다.
            vm.CommitCommand.Execute(null);

            Assert.That(vm.History.Entries.Select(e => e.ShortSha), Is.EqualTo(new[] { "a3f9c2b" }));
            Assert.That(vm.History.ScopeLabel, Is.EqualTo("저장소 전체"));
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
        public void GenerateRollbackScriptCommand_NotifiesAndSkipsSave_WhenNoObjectHasAPreviousRevision()
        {
            var vm = NewViewModelWithOneCheckedChange(out _);
            _git.Setup(g => g.GetFileContentBeforeLastCommit(Server, Database, It.IsAny<string>()))
                .Returns((string?)null);

            vm.GenerateRollbackScriptCommand.Execute(null);

            Assert.That(_saveDialog.CallCount, Is.EqualTo(0), "저장할 내용이 없으면 대화상자를 띄우지 않아야 합니다");
            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("dbo.Users"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("이전 리비전이 없어"));
            Assert.That(vm.WarningMessage, Is.Null,
                "일회성 동작의 결과를 지속 상태 배너에 쓰면 안 됩니다");
            Assert.That(_notifier.Errors, Is.Empty, "내보낼 내용이 없는 것은 오류가 아닙니다");
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

            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1));
            Assert.That(_notifier.InfoCalls[0].Title, Is.EqualTo("DBVC 배포 스크립트"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("1개 객체를 내보냈습니다"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("dbo.Gone"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("추출된 파일이 없어"),
                "Deployment의 제외 사유는 이전 리비전이 아니라 추출된 파일입니다");
            Assert.That(vm.WarningMessage, Is.Null);
        }

        [Test]
        public void GenerateDeploymentScriptCommand_NotifiesSuccess_EvenWhenNothingWasExcluded()
        {
            var vm = NewViewModelWithOneCheckedChange(out var repoPath);
            _saveDialog.PathToReturn = Path.Combine(repoPath, "deploy.sql");

            vm.GenerateDeploymentScriptCommand.Execute(null);

            Assert.That(_notifier.InfoCalls, Has.Count.EqualTo(1),
                "성공했는데 아무 피드백이 없으면 사용자는 저장됐는지 알 수 없습니다");
            Assert.That(_notifier.InfoCalls[0].Message, Does.Contain("1개 객체를 내보냈습니다"));
            Assert.That(_notifier.InfoCalls[0].Message, Does.Not.Contain("제외"),
                "제외가 없으면 제외 문구를 붙이지 않습니다");
        }

        [Test]
        public void GenerateDeploymentScriptCommand_DoesNotNotify_WhenTheUserCancelsTheSaveDialog()
        {
            var vm = NewViewModelWithOneCheckedChange(out _);
            _saveDialog.PathToReturn = null;

            vm.GenerateDeploymentScriptCommand.Execute(null);

            Assert.That(_notifier.InfoCalls, Is.Empty, "취소는 오류도 아니고 완료도 아닙니다");
            Assert.That(_notifier.Errors, Is.Empty);
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

        // ---------- UI 스레드 차단 방지 ----------
        //
        // Refresh는 SMO로 DB 전 객체를 추출하고 libgit2로 작업 트리 상태를 읽는다. Commit은
        // 객체 3000개 기준 스테이징에만 15초가 걸린다. 이것들을 명령이 실행되는 스레드에서
        // 그대로 하면 그 시간 동안 SSMS 전체(메뉴·쿼리 편집기·개체 탐색기)가 멈춘다.
        // 아래 테스트들은 무거운 호출이 호출자 스레드가 아니라 스케줄러로 넘어가는지 본다.

        /// <summary>
        /// 넘겨받은 작업을 붙잡아 두었다가 테스트가 <see cref="RunPending"/>을 부를 때 실행한다.
        /// 인라인으로 실행해 버리면 "호출자 스레드에서 하지 않는다"를 증명할 수 없다.
        /// </summary>
        private sealed class DeferredScheduler : IBackgroundScheduler
        {
            private readonly List<Action> _pending = new List<Action>();

            public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
            {
                _pending.Add(() =>
                {
                    T value;
                    try { value = work(); }
                    catch (Exception ex) { onFailed(ex); return; }
                    onSucceeded(value);
                });
            }

            public void Post(Action action) => action();

            /// <summary>대기 중인 작업을 모두 실행한다. 실행 중 새로 등록된 작업도 이어서 처리한다.</summary>
            public void RunPending()
            {
                while (_pending.Count > 0)
                {
                    var next = _pending[0];
                    _pending.RemoveAt(0);
                    next();
                }
            }
        }

        private ViewChangesViewModel NewViewModel(IBackgroundScheduler scheduler)
        {
            return new ViewChangesViewModel(
                _config.Object, _stateTracker.Object, _git.Object, _smo.Object, _notifier, _saveDialog,
                _cleaner.Object, _connectDialog, _credentials.Object, _ssms.Object, scheduler);
        }

        /// <summary>Connect까지 끝낸 ViewModel. Connect 자체도 스케줄러를 타므로 여기서 비워 준다.</summary>
        private ViewChangesViewModel NewConnectedViewModel(DeferredScheduler scheduler)
        {
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            var vm = NewViewModel(scheduler);
            vm.ConnectCommand.Execute(null);
            scheduler.RunPending();
            return vm;
        }

        [Test]
        public void Connect_HandsTheConnectionProbeToTheScheduler()
        {
            var scheduler = new DeferredScheduler();
            _ssms.Setup(s => s.TryGetCurrent()).Returns(Info());
            var vm = NewViewModel(scheduler);

            vm.ConnectCommand.Execute(null);

            _stateTracker.Verify(s => s.TestConnection(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "접속 시도는 호출자(UI) 스레드가 아니라 스케줄러에 넘겨야 한다");

            scheduler.RunPending();

            _stateTracker.Verify(s => s.TestConnection(Server, Database), Times.Once);
        }

        [Test]
        public void Refresh_HandsScriptingToTheScheduler()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);
            _smo.Invocations.Clear();

            vm.RefreshCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()),
                Times.Never, "SMO 추출은 호출자(UI) 스레드가 아니라 스케줄러에 넘겨야 한다");

            scheduler.RunPending();

            _smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Refresh_PopulatesChanges_WhenBackgroundWorkCompletes()
        {
            var scheduler = new DeferredScheduler();
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database))
                .Returns(new List<ChangeRecord> { Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql") });
            var vm = NewConnectedViewModel(scheduler);

            vm.RefreshCommand.Execute(null);
            scheduler.RunPending();

            Assert.That(vm.Changes.Select(c => c.ObjectName), Is.EqualTo(new[] { "dbo.Users" }));
        }

        [Test]
        public void IsBusy_IsTrueWhileRefreshWorkIsOutstanding()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);

            vm.RefreshCommand.Execute(null);
            Assert.That(vm.IsBusy, Is.True, "작업이 끝나기 전에는 진행 중임을 화면이 알 수 있어야 한다");

            scheduler.RunPending();
            Assert.That(vm.IsBusy, Is.False);
        }

        [Test]
        public void RefreshCommand_CannotExecute_WhileWorkIsOutstanding()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);

            vm.RefreshCommand.Execute(null);

            Assert.That(vm.RefreshCommand.CanExecute(null), Is.False,
                "진행 중에 다시 누르면 같은 추출이 겹쳐 돈다");

            scheduler.RunPending();
            Assert.That(vm.RefreshCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void Refresh_ReleasesBusyAndReportsError_WhenBackgroundWorkThrows()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Throws(new InvalidOperationException("추출 중 폭발"));

            vm.RefreshCommand.Execute(null);
            scheduler.RunPending();

            Assert.That(_notifier.Errors, Has.Some.Contains("추출 중 폭발"));
            Assert.That(vm.IsBusy, Is.False, "실패해도 잠금이 풀려야 다시 시도할 수 있다");
        }

        [Test]
        public void Commit_HandsGitWorkToTheScheduler()
        {
            var scheduler = new DeferredScheduler();
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database))
                .Returns(new List<ChangeRecord> { Record("dbo", "Users", "Modified", "dbo/Tables/Users.sql") });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(GitCommitResult.Committed);

            var vm = NewConnectedViewModel(scheduler);
            vm.RefreshCommand.Execute(null);
            scheduler.RunPending();
            vm.CommitMessage = "첫 커밋";

            vm.CommitCommand.Execute(null);

            _git.Verify(g => g.CommitChanges(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()),
                Times.Never, "스테이징·커밋은 호출자(UI) 스레드가 아니라 스케줄러에 넘겨야 한다");

            scheduler.RunPending();

            _git.Verify(g => g.CommitChanges(Server, Database, "첫 커밋", It.IsAny<IEnumerable<string>>()), Times.Once);
        }

        // ---------- Pull·Push도 UI 스레드 밖에서 ----------
        //
        // 이 둘은 네트워크와 ssh 프로세스가 걸리는 구간이다. 원격이 응답하지 않으면 그동안
        // SSMS 전체가 멈춘다. 새로고침·커밋과 같은 이유로 스케줄러를 타야 한다.

        [Test]
        public void Pull_HandsTheNetworkWorkToTheScheduler()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);
            _git.Invocations.Clear();

            vm.PullCommand.Execute(null);

            _git.Verify(g => g.PullChanges(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "Pull은 호출자(UI) 스레드가 아니라 스케줄러에 넘겨야 한다");

            scheduler.RunPending();

            _git.Verify(g => g.PullChanges(Server, Database), Times.Once);
        }

        [Test]
        public void IsBusy_IsTrueWhilePullWorkIsOutstanding()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);

            vm.PullCommand.Execute(null);
            Assert.That(vm.IsBusy, Is.True);

            scheduler.RunPending();
            Assert.That(vm.IsBusy, Is.False);
        }

        [Test]
        public void PullCommand_CannotExecute_WhileWorkIsOutstanding()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);

            vm.PullCommand.Execute(null);

            Assert.That(vm.PullCommand.CanExecute(null), Is.False,
                "병합과 추출이 같은 작업 트리를 동시에 건드리면 안 된다");

            scheduler.RunPending();
            Assert.That(vm.PullCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void Pull_ReleasesBusyAndReportsError_WhenBackgroundWorkThrows()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);
            _git.Setup(g => g.PullChanges(Server, Database)).Throws(new InvalidOperationException("원격이 폭발"));

            vm.PullCommand.Execute(null);
            scheduler.RunPending();

            Assert.That(_notifier.ErrorCalls, Has.Some.Matches<(string Title, string Message)>(
                c => c.Title == "DBVC Pull 실패" && c.Message.Contains("원격이 폭발")));
            Assert.That(vm.IsBusy, Is.False, "실패해도 잠금이 풀려야 다시 시도할 수 있다");
        }

        [Test]
        public void Pull_KeepsTheInterruptionWording_WhenTheBackgroundMergeConflicts()
        {
            // 갈래별 제목이 스케줄러를 거치면서 뭉개지면 사용자는 '중단'과 '실패'를 구분하지 못한다.
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);
            _git.Setup(g => g.PullChanges(Server, Database)).Throws(new MergeConflictException("충돌이 나서 되돌렸습니다"));

            vm.PullCommand.Execute(null);
            scheduler.RunPending();

            Assert.That(_notifier.ErrorCalls, Has.Some.Matches<(string Title, string Message)>(
                c => c.Title == "DBVC Pull 중단" && c.Message.Contains("충돌이 나서 되돌렸습니다")));
            Assert.That(vm.IsBusy, Is.False);
        }

        [Test]
        public void Push_HandsTheNetworkWorkToTheScheduler()
        {
            var scheduler = new DeferredScheduler();
            _git.Setup(g => g.HasCommitsToPush(Server, Database)).Returns(true);
            var vm = NewConnectedViewModel(scheduler);
            _git.Invocations.Clear();

            vm.PushCommand.Execute(null);

            _git.Verify(g => g.PushChanges(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
                "Push는 호출자(UI) 스레드가 아니라 스케줄러에 넘겨야 한다");

            scheduler.RunPending();

            _git.Verify(g => g.PushChanges(Server, Database), Times.Once);
        }

        [Test]
        public void PushCommand_CannotExecute_WhileWorkIsOutstanding()
        {
            var scheduler = new DeferredScheduler();
            _git.Setup(g => g.HasCommitsToPush(Server, Database)).Returns(true);
            var vm = NewConnectedViewModel(scheduler);

            vm.PushCommand.Execute(null);

            Assert.That(vm.PushCommand.CanExecute(null), Is.False);

            scheduler.RunPending();
            Assert.That(vm.PushCommand.CanExecute(null), Is.True);
        }

        [Test]
        public void Push_ReleasesBusyAndReportsError_WhenBackgroundWorkThrows()
        {
            var scheduler = new DeferredScheduler();
            _git.Setup(g => g.HasCommitsToPush(Server, Database)).Returns(true);
            var vm = NewConnectedViewModel(scheduler);
            _git.Setup(g => g.PushChanges(Server, Database)).Throws(new InvalidOperationException("원격이 거부"));

            vm.PushCommand.Execute(null);
            scheduler.RunPending();

            Assert.That(_notifier.ErrorCalls, Has.Some.Matches<(string Title, string Message)>(
                c => c.Title == "DBVC Push 실패" && c.Message.Contains("원격이 거부")));
            Assert.That(vm.IsBusy, Is.False);
        }

        [Test]
        public void CancelCommand_IsUnavailable_WhenTheOutstandingWorkCannotBeCancelled()
        {
            // Cancel()이 취소하는 것은 추출용 토큰뿐이다. Pull 중에 버튼이 뜨면 눌러도
            // 아무 일이 없고 "취소하는 중..."만 남는다 — 없는 취소를 있는 척하게 된다.
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);

            vm.PullCommand.Execute(null);

            Assert.That(vm.IsBusy, Is.True);
            Assert.That(vm.CancelCommand.CanExecute(null), Is.False,
                "취소할 수 없는 작업에 취소 버튼을 보이면 안 된다");

            scheduler.RunPending();
        }

        // ---------- 변경분만 추출 ----------
        //
        // 새로고침은 DB 전체를 다시 스크립팅했다. 객체 1000개짜리 DB에서 이것은 SQL 왕복
        // 수천 번이고, 실측에서 열거 단계만으로도 수 분이 걸린다(CPU가 아니라 I/O 대기).
        // DDL 로그는 무엇이 바뀌었는지 이미 알고 있으므로 그 목록만 추출한다.
        //
        // 다만 기준선이 없으면(저장소를 갓 연결한 직후) 변경분만 추출해서는 안 된다 —
        // 나머지 객체의 파일이 저장소에 없어 사용자가 커밋할 것을 찾지 못한다.

        /// <summary>규약에 맞는 .sql이 하나 들어 있는 실제 폴더를 만들어 기준선이 있는 상태로 만든다.</summary>
        private string NewRepoWithBaseline()
        {
            var root = Path.Combine(Path.GetTempPath(), "dbvc_vm_" + Guid.NewGuid().ToString("N"));
            var dir = Path.Combine(root, "dbo", "Tables");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Seed.sql"), "-- seed");
            _tempDirs.Add(root);

            _config.Setup(c => c.TryGetMapping(Server, Database))
                .Returns(new MappingConfig { ServerName = Server, DatabaseName = Database, GitPath = root });
            return root;
        }

        [Test]
        public void Refresh_ExtractsOnlyTheObjectsTheChangeLogNames_WhenABaselineExists()
        {
            NewRepoWithBaseline();
            _stateTracker.Setup(s => s.GetChangedObjectNames(Server, Database))
                .Returns(new List<string> { "dbo.Users", "sales.Orders" });

            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();
            vm.RefreshCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(Server, Database,
                It.Is<List<string>>(l => l != null && l.Count == 2 && l.Contains("dbo.Users") && l.Contains("sales.Orders")), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()),
                Times.Once);
            _smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never,
                "기준선이 있으면 전체를 다시 추출해서는 안 된다");
        }

        [Test]
        public void Refresh_ExtractsEverything_WhenThereIsNoBaselineYet()
        {
            // 매핑 경로에 추출물이 없다. 변경분만 뽑으면 저장소가 빈 채로 남는다.
            _stateTracker.Setup(s => s.GetChangedObjectNames(Server, Database))
                .Returns(new List<string> { "dbo.Users" });

            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();
            vm.RefreshCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Refresh_SkipsExtractionEntirely_WhenTheChangeLogIsEmpty()
        {
            NewRepoWithBaseline();
            _stateTracker.Setup(s => s.GetChangedObjectNames(Server, Database)).Returns(new List<string>());

            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();
            vm.RefreshCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "빈 목록을 그대로 넘기면 필터가 없는 것으로 해석되어 전체가 추출된다");
        }

        [Test]
        public void RefreshAll_ExtractsEverything_EvenWhenABaselineExists()
        {
            NewRepoWithBaseline();
            _stateTracker.Setup(s => s.GetChangedObjectNames(Server, Database))
                .Returns(new List<string> { "dbo.Users" });

            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();
            vm.RefreshAllCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Once,
                "DDL 로그가 놓친 변경을 되찾을 수 있는 유일한 경로다");
        }

        [Test]
        public void Setup_ExtractsEverything_BecauseChangesBeforeTheTriggerAreNotInTheLog()
        {
            NewRepoWithBaseline();
            _stateTracker.Setup(s => s.GetChangedObjectNames(Server, Database))
                .Returns(new List<string> { "dbo.Users" });

            var vm = NewConnectedViewModel();
            _smo.Invocations.Clear();
            vm.SetupCommand.Execute(null);

            _smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // ---------- 진행률과 취소 ----------

        [Test]
        public void ProgressText_ReportsHowFarTheExtractionHasGot()
        {
            _smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null,
                    It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string __, List<string>? ___, IProgress<ExtractionProgress>? progress, CancellationToken ____) =>
                {
                    progress!.Report(new ExtractionProgress(7, 20, "dbo.Users"));
                    return new ScriptResult();
                });

            var vm = NewConnectedViewModel();
            string? seen = null;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ProgressText) && vm.ProgressText != null) seen = vm.ProgressText; };

            vm.RefreshCommand.Execute(null);

            Assert.That(seen, Does.Contain("7/20").And.Contain("dbo.Users"));
        }

        [Test]
        public void ProgressText_IsClearedWhenTheWorkFinishes()
        {
            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);
            Assert.That(vm.ProgressText, Is.Null);
        }

        [Test]
        public void Cancel_CancelsTheTokenHandedToTheExtraction()
        {
            var scheduler = new DeferredScheduler();
            CancellationToken captured = default;
            _smo.Setup(s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
                    It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string __, List<string>? ___, IProgress<ExtractionProgress>? ____, CancellationToken token) =>
                {
                    captured = token;
                    return new ScriptResult();
                });

            var vm = NewConnectedViewModel(scheduler);
            vm.RefreshCommand.Execute(null);

            vm.CancelCommand.Execute(null);
            scheduler.RunPending();

            Assert.That(captured.IsCancellationRequested, Is.True,
                "추출이 받은 토큰이 취소되지 않으면 멈출 방법이 없다");
        }

        [Test]
        public void CancelCommand_IsOnlyAvailableWhileWorkIsOutstanding()
        {
            var scheduler = new DeferredScheduler();
            var vm = NewConnectedViewModel(scheduler);

            Assert.That(vm.CancelCommand.CanExecute(null), Is.False);

            vm.RefreshCommand.Execute(null);
            Assert.That(vm.CancelCommand.CanExecute(null), Is.True);

            scheduler.RunPending();
            Assert.That(vm.CancelCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void Refresh_DoesNotReportAnError_WhenTheUserCancels()
        {
            // 사용자가 누른 취소를 오류 상자로 되돌려주면 자기가 한 일을 고장으로 읽는다.
            _smo.Setup(s => s.ScriptObjectsDetailed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(),
                    It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Throws(new OperationCanceledException());

            var vm = NewConnectedViewModel();
            vm.RefreshCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty);
            Assert.That(vm.WarningMessage, Does.Contain("취소"));
            Assert.That(vm.IsBusy, Is.False);
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
            public List<string> Infos { get; } = new List<string>();

            /// <summary>ShowInfo에 실제로 전달된 (title, message) 쌍.</summary>
            public List<(string Title, string Message)> InfoCalls { get; } = new List<(string, string)>();

            /// <summary>
            /// ShowError에 실제로 전달된 (title, message) 쌍.
            /// Errors는 message만 담아 기존 테스트를 그대로 두는데, 그것만으로는
            /// title이 다른 두 catch 분기(예: 병합 충돌 vs. 예기치 못한 실패)를
            /// 구분해서 검증할 수 없다.
            /// </summary>
            public List<(string Title, string Message)> ErrorCalls { get; } = new List<(string, string)>();

            /// <summary>Confirm의 응답. 기본이 "계속"이라 기존 테스트의 동작이 바뀌지 않는다.</summary>
            public bool ConfirmResult { get; set; } = true;
            public int ConfirmCallCount { get; private set; }

            /// <summary>Confirm에 실제로 전달된 (title, message) 쌍. 문구 자체를 검증할 때 쓴다.</summary>
            public List<(string Title, string Message)> ConfirmCalls { get; } = new List<(string, string)>();

            public void ShowError(string title, string message)
            {
                Errors.Add(message);
                ErrorCalls.Add((title, message));
            }

            public void ShowInfo(string title, string message)
            {
                Infos.Add(message);
                InfoCalls.Add((title, message));
            }

            public bool Confirm(string title, string message)
            {
                ConfirmCallCount++;
                ConfirmCalls.Add((title, message));
                return ConfirmResult;
            }
        }

        private sealed class RecordingConnectDialog : IRepositoryConnectDialog
        {
            public RepositoryConnectRequest? RequestToReturn { get; set; }
            public int CallCount { get; private set; }

            public RepositoryConnectRequest? Prompt(string serverName, string databaseName)
            {
                CallCount++;
                return RequestToReturn;
            }
        }

        [Test]
        public void CurrentBranch_ShowsRepositoryBranch_WhenConnected()
        {
            // 비교 기준이 브랜치 내용이므로 어느 브랜치인지 모르면 diff를 오독한다.
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState { CurrentBranch = "feature/x", BlockReason = RepositoryBlockReason.None });

            var vm = NewConnectedViewModel();

            Assert.That(vm.CurrentBranch, Is.EqualTo("feature/x"));
        }

        [Test]
        public void IsBlocked_IsTrueWithMessage_WhenRepositoryStateIsBlocked()
        {
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState
                {
                    CurrentBranch = "develop",
                    BlockReason = RepositoryBlockReason.BranchMismatch,
                    BlockMessage = "이 대상은 'master' 브랜치에 고정되어 있는데 저장소는 'develop'에 있습니다."
                });

            var vm = NewConnectedViewModel();

            Assert.That(vm.IsBlocked, Is.True);
            Assert.That(vm.BlockMessage, Does.Contain("master"));
        }

        [Test]
        public void CommitCommand_CannotExecute_WhenBlocked()
        {
            // 차단은 경고가 아니다. 조용히 틀린 결과를 내는 것보다 멈추는 편이 낫다.
            _git.Setup(g => g.GetRepositoryState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new RepositoryState
                {
                    BlockReason = RepositoryBlockReason.DetachedHead,
                    BlockMessage = "저장소가 어느 브랜치도 가리키지 않는 상태(detached HEAD)입니다."
                });

            var vm = NewConnectedViewModel();

            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void ShowAllAuthors_DefaultsToFalse()
        {
            // 기본이 전체면 목록에 남의 진행 중 작업이 전부 뜨고, 전체 선택 커밋 한 번이면
            // 검증되지 않은 남의 작업이 브랜치에 담긴다.
            Assert.That(NewViewModel().ShowAllAuthors, Is.False);
        }

        [Test]
        public void Refresh_PassesShowAllAuthorsToTracker()
        {
            var vm = NewConnectedViewModel();
            vm.ShowAllAuthors = true;

            _stateTracker.Verify(s => s.RefreshState(Server, Database, true), Times.AtLeastOnce);
        }

        /// <summary>
        /// dbo.P 하나가 선택된 상태의 뷰모델. 항목은 새로고침이 기본으로 체크해 둔다.
        /// </summary>
        private ViewChangesViewModel NewViewModelWithOneSelectedChange(string qualifiedName)
        {
            var parts = qualifiedName.Split('.');
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record(parts[0], parts[1], "Modified", $"{parts[0]}/StoredProcedures/{parts[1]}.sql")
            });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(GitCommitResult.Committed);

            var vm = NewConnectedViewModel();
            vm.Changes.Single(ch => ch.ObjectName == qualifiedName).IsSelected = true;
            return vm;
        }

        [Test]
        public void Commit_ClosesTheLogRows_WhenTheFilesAlreadyMatchTheRepository()
        {
            // 남이 만진 행이 내 커밋에 이미 담긴 내용과 같으면 스테이징할 차이가 없다.
            // 그때 로그 행을 닫지 않으면 그 항목은 전체 보기에 영원히 남는다 - 다시 커밋해도
            // 또 차이가 없어 "커밋할 변경사항이 없습니다"만 반복되고, 지울 방법이 없다.
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Orders", "Modified", "dbo/Tables/Orders.sql")
            });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(GitCommitResult.NothingToCommit);

            var vm = NewConnectedViewModel();
            vm.Changes.Single(c => c.ObjectName == "dbo.Orders").IsSelected = true;
            vm.CommitMessage = "이미 같은 내용";

            vm.CommitCommand.Execute(null);

            _stateTracker.Verify(s => s.MarkProcessed(Server, Database,
                It.Is<IEnumerable<ChangeRecord>>(records => records.Any(r => r.QualifiedName == "dbo.Orders"))),
                Times.Once);
        }

        [Test]
        public void Commit_DoesNotCloseTheLogRows_WhenTheRepositoryCannotBeFound()
        {
            // "커밋할 것이 없다"와 "커밋할 수 없다"를 같이 다루면, 매핑이 끊긴 상태에서 누른
            // 커밋이 로그를 닫아 버려 그 변경이 어디에도 기록되지 않은 채 사라진다.
            _stateTracker.Setup(s => s.GetPendingChanges(Server, Database)).Returns(new List<ChangeRecord>
            {
                Record("dbo", "Orders", "Modified", "dbo/Tables/Orders.sql")
            });
            _git.Setup(g => g.CommitChanges(Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(GitCommitResult.NotMapped);

            var vm = NewConnectedViewModel();
            vm.Changes.Single(c => c.ObjectName == "dbo.Orders").IsSelected = true;
            vm.CommitMessage = "매핑 없음";

            vm.CommitCommand.Execute(null);

            _stateTracker.Verify(s => s.MarkProcessed(Server, Database, It.IsAny<IEnumerable<ChangeRecord>>()),
                Times.Never);
        }

        [Test]
        public void Commit_AsksForConfirmation_WhenAnotherAuthorTouchedTheSameObject()
        {
            _stateTracker.Setup(s => s.GetCoAuthorWarnings(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(new[] { new CoAuthorWarning { QualifiedName = "dbo.P", Author = "KIM-PC" } });

            _notifier.ConfirmResult = false;

            var vm = NewViewModelWithOneSelectedChange("dbo.P");
            vm.CommitMessage = "테스트";
            vm.CommitCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Any(call => call.Message.Contains("KIM-PC")), Is.True,
                "다른 작업자의 PC 이름이 확인 문구에 없습니다");

            // 사용자가 취소했으므로 커밋이 일어나면 안 된다.
            _git.Verify(g => g.CommitChanges(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()),
                Times.Never);
        }

        [Test]
        public void Commit_ProceedsAfterConfirmation_WhenTheUserAccepts()
        {
            // 경고는 차단이 아니다. 대부분은 실제로 이어서 작업한 정상적인 경우다.
            _stateTracker.Setup(s => s.GetCoAuthorWarnings(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(new[] { new CoAuthorWarning { QualifiedName = "dbo.P", Author = "KIM-PC" } });

            _notifier.ConfirmResult = true;

            var vm = NewViewModelWithOneSelectedChange("dbo.P");
            vm.CommitMessage = "테스트";
            vm.CommitCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.EqualTo(1),
                "확인을 건너뛰고 곧장 커밋했다면 이 테스트는 아무것도 보장하지 않는다");
            _git.Verify(g => g.CommitChanges(
                Server, Database, It.IsAny<string>(), It.IsAny<IEnumerable<string>>()),
                Times.Once);
        }

        [Test]
        public void Commit_DoesNotAsk_WhenNoOtherAuthorTouchedIt()
        {
            _stateTracker.Setup(s => s.GetCoAuthorWarnings(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
                .Returns(new CoAuthorWarning[0]);

            var before = _notifier.ConfirmCallCount;

            var vm = NewViewModelWithOneSelectedChange("dbo.P");
            vm.CommitMessage = "테스트";
            vm.CommitCommand.Execute(null);

            Assert.That(_notifier.ConfirmCallCount, Is.EqualTo(before),
                "경고할 것이 없는데 확인을 물었습니다 - 매번 뜨면 사용자가 읽지 않게 된다");
        }
    }
}
