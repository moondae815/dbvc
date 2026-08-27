using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.Services;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 배포는 3단계 루프다 — 차이를 보고, 스크립트를 만들어 사람이 실행하고, 다시 검사한다.
    /// 3단계가 없으면 "됐다고 생각했는데 안 된" 배포가 성공으로 보인다.
    /// </summary>
    [TestFixture]
    public class DeploymentViewModelTests
    {
        private const string Server = "TestServer";
        private const string Database = "TestDb";

        private Mock<IConfigManager> _config = null!;
        private Mock<IGitManager> _git = null!;
        private Mock<ISmoManager> _smo = null!;
        private RecordingNotifier _notifier = null!;
        private RecordingSaveDialog _saveDialog = null!;
        private BusyState _busy = null!;
        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dbvc_dep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private DeploymentViewModel NewViewModel(MappingMode mode, out string repoPath)
        {
            repoPath = NewTempDir();
            var mapping = new MappingConfig
            {
                ServerName = Server,
                DatabaseName = Database,
                GitPath = repoPath,
                Mode = mode,
                Branch = mode == MappingMode.Audit ? "master" : "develop"
            };

            _config = new Mock<IConfigManager>();
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns(mapping);
            _git = new Mock<IGitManager>();
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            _smo = new Mock<ISmoManager>();
            _notifier = new RecordingNotifier();
            _saveDialog = new RecordingSaveDialog();
            _busy = new BusyState();

            var vm = new DeploymentViewModel(
                _config.Object, _git.Object, _smo.Object,
                new ScriptExporter(_config.Object, _git.Object),
                _notifier, _saveDialog, new InlineBackgroundScheduler(), _busy);

            vm.SetTarget(Server, Database, mode);
            return vm;
        }

        private static ComparisonResult ResultWith(params SchemaDifference[] differences)
        {
            var result = new ComparisonResult { ComparedCount = 10 };
            result.Differences.AddRange(differences);
            return result;
        }

        [Test]
        public void CompareCommand_FillsTheList_WhenDifferencesAreFound()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified)));

            vm.CompareCommand.Execute(null);

            Assert.That(vm.Differences.Count, Is.EqualTo(1));
            Assert.That(vm.Differences[0].StateText, Is.EqualTo("배포 필요 (내용 다름)"));
            Assert.That(vm.SummaryText, Does.Contain("10").And.Contain("1"));
        }

        [Test]
        public void CompareCommand_PullsBeforeComparing()
        {
            // 로컬 develop이 낡았으면 방금 병합된 변경이 목록에서 통째로 빠지고,
            // 그것은 "배포 완료"로 보인다.
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith());

            vm.CompareCommand.Execute(null);

            _git.Verify(g => g.PullChanges(Server, Database), Times.Once);
        }

        [Test]
        public void CompareCommand_ReportsInSync_WhenNothingDiffers()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith());

            vm.CompareCommand.Execute(null);

            Assert.That(vm.Differences, Is.Empty);
            Assert.That(vm.SummaryText, Does.Contain("일치"));
        }

        [Test]
        public void CompareCommand_StopsAndReports_WhenPullFails()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _git.Setup(g => g.PullChanges(Server, Database)).Throws(new GitRemoteException("원격에 연결할 수 없습니다."));

            vm.CompareCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Not.Empty);
            _smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void SetTarget_ClearsPreviousResults()
        {
            // 낡은 결과를 최신인 척 보여주지 않는다. 원격 확인 표시와 같은 규칙이다.
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(new SchemaDifference("dbo.A", "dbo/Views/A.sql", "View", ObjectDiffState.Modified)));
            vm.CompareCommand.Execute(null);

            vm.SetTarget("OtherServer", "OtherDb", MappingMode.Audit);

            Assert.That(vm.Differences, Is.Empty);
            Assert.That(vm.SummaryText, Is.Null);
            Assert.That(vm.HasResult, Is.False);
        }

        [Test]
        public void SaveScriptCommand_IsDisabled_UntilComparisonHasRun()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);

            Assert.That(vm.SaveScriptCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void SaveScriptCommand_WritesTheScript_AndReportsExclusions()
        {
            var vm = NewViewModel(MappingMode.Deploy, out var repoPath);
            var procPath = Path.Combine(repoPath, "dbo", "StoredProcedures");
            Directory.CreateDirectory(procPath);
            File.WriteAllText(Path.Combine(procPath, "GetUser.sql"), "CREATE OR ALTER PROCEDURE dbo.GetUser AS SELECT 1");

            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(
                    new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified),
                    new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.Modified)));
            vm.CompareCommand.Execute(null);

            _saveDialog.PathToReturn = Path.Combine(NewTempDir(), "deploy.sql");
            vm.SaveScriptCommand.Execute(null);

            var written = File.ReadAllText(_saveDialog.PathToReturn);
            Assert.That(written, Does.Contain("CREATE OR ALTER PROCEDURE dbo.GetUser"));
            Assert.That(written, Does.Contain("수동 변경이 필요합니다: 1 (dbo.Orders)"));
            Assert.That(_notifier.Infos, Is.Not.Empty);
        }

        [Test]
        public void SaveScriptCommand_ReportsNothingToWrite_WhenEveryObjectIsExcluded()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith(new SchemaDifference("dbo.Orders", "dbo/Tables/Orders.sql", "Table", ObjectDiffState.Modified)));
            vm.CompareCommand.Execute(null);

            vm.SaveScriptCommand.Execute(null);

            Assert.That(_saveDialog.WasPrompted, Is.False);
            Assert.That(_notifier.Infos.Concat(_notifier.Errors).Any(m => m.Contains("생성할")), Is.True);
        }

        [Test]
        public void Commands_AreDisabled_WhileTheSharedBusyStateIsSet()
        {
            // 같은 저장소와 같은 접속을 쓰므로 변경 목록 화면이 일하는 동안 겹쳐 돌면 안 된다.
            var vm = NewViewModel(MappingMode.Deploy, out _);

            _busy.IsBusy = true;

            Assert.That(vm.CompareCommand.CanExecute(null), Is.False);
        }
    }
}
