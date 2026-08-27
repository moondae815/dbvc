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
            return NewViewModel(mode, out repoPath, new InlineBackgroundScheduler());
        }

        private DeploymentViewModel NewViewModel(MappingMode mode, out string repoPath, IBackgroundScheduler scheduler)
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
                _notifier, _saveDialog, scheduler, _busy);

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
        public void CompareCommand_KeepsFailedObjectsOutOfDifferences_AndReportsThemSeparately()
        {
            // FailedObjects는 "차이가 없다"가 아니라 "모른다"이다. 목록에 섞이면 배포 대상으로 읽힌다.
            var vm = NewViewModel(MappingMode.Deploy, out _);
            var result = ResultWith(new SchemaDifference("dbo.A", "dbo/Views/A.sql", "View", ObjectDiffState.Modified));
            result.FailedObjects.Add("dbo.Broken");
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(result);

            vm.CompareCommand.Execute(null);

            Assert.That(vm.Differences.Select(d => d.QualifiedName), Does.Not.Contain("dbo.Broken"));
            Assert.That(vm.Differences.Count, Is.EqualTo(1));
            Assert.That(_notifier.Errors.Any(m => m.Contains("dbo.Broken")), Is.True);
        }

        [Test]
        public void CompareCommand_DoesNotClaimMatch_WhenObjectsFailedButNoDifferencesFound()
        {
            // 판정하지 못한 객체가 있는데 "일치합니다"만 읽히면 사용자는 배포가 끝났다고 착각한다.
            var vm = NewViewModel(MappingMode.Deploy, out _);
            var result = ResultWith();
            result.FailedObjects.Add("dbo.Broken1");
            result.FailedObjects.Add("dbo.Broken2");
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(result);

            vm.CompareCommand.Execute(null);

            Assert.That(vm.SummaryText, Is.Not.EqualTo("대상 10개를 검사했습니다. 브랜치와 일치합니다."));
            Assert.That(vm.SummaryText, Does.Contain("2"));
            Assert.That(vm.SummaryText, Does.Contain("판정하지 못했습니다"));
        }

        [Test]
        public void CompareCommand_ReportsBothDifferencesAndFailures_WhenBothArePresent()
        {
            var vm = NewViewModel(MappingMode.Deploy, out _);
            var result = ResultWith(new SchemaDifference("dbo.A", "dbo/Views/A.sql", "View", ObjectDiffState.Modified));
            result.FailedObjects.Add("dbo.Broken");
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(result);

            vm.CompareCommand.Execute(null);

            Assert.That(vm.SummaryText, Does.Contain("1개가 다릅니다"));
            Assert.That(vm.SummaryText, Does.Contain("1개는 판정하지 못했습니다"));
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
        public void CompareCommand_DoesNotClaimMatch_WhenTheRepositoryScanWasIncomplete()
        {
            // 권한 없는 폴더 하나를 만나면 Directory.EnumerateFiles가 순회를 통째로 멈춘다.
            // 그 아래의 "브랜치에만 있음"이 전부 사라진 채 "일치합니다"가 뜨면, 이 기능이
            // 믿을 수 있게 만들려던 유일한 문장이 거짓말이 된다.
            var vm = NewViewModel(MappingMode.Audit, out _);
            var result = ResultWith();
            result.RepositoryScanCompleted = false;
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(result);

            vm.CompareCommand.Execute(null);

            Assert.That(vm.SummaryText, Does.Not.Contain("일치합니다"));
            Assert.That(vm.SummaryText, Does.Contain("저장소를 전부 읽지 못"));
        }

        [Test]
        public void CompareCommand_SkipsThePull_WhenTheRepositoryHasNoRemote()
        {
            // 원격 없이 기존 폴더를 배포 클론으로 채택하는 것은 대화상자가 안내하는 정상
            // 경로다. 거기서 멈추면 패널의 유일한 버튼이 언제나 오류를 내고 화면이 쓸모없어진다.
            var vm = NewViewModel(MappingMode.Deploy, out _);
            _git.Setup(g => g.PullChanges(Server, Database))
                .Throws(new GitRemoteNotConfiguredException("원격(remote)이 설정되어 있지 않아 Pull할 수 없습니다."));
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(ResultWith());

            vm.CompareCommand.Execute(null);

            Assert.That(_notifier.Errors, Is.Empty, "건너뛰어야 할 상황을 실패로 알렸다");
            Assert.That(vm.SummaryText, Is.Not.Null, "Pull이 없다는 이유로 비교까지 멈췄다");
            _smo.Verify(s => s.CompareWithRepository(
                Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
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

        // ---------- diff 본문 ----------

        [Test]
        public void LoadSelectedTexts_ReadsOffTheUiThread_AndReportsBusyWhileItRuns()
        {
            // 동기로 두면 목록 클릭 한 번마다 SMO 접속과 DB 전체 열거가 UI 스레드에서 돈다.
            // 객체 200개짜리 DB에서 열거만 871 ms다.
            var scheduler = new DeferredScheduler();
            var vm = NewViewModel(MappingMode.Deploy, out var repoPath, scheduler);
            SelectOneDifference(vm, repoPath, "브랜치 원문");
            _smo.Setup(s => s.ScriptObjectToText(Server, Database, "dbo.P")).Returns("DB 원문");

            string? branch = null;
            vm.LoadSelectedTexts((b, d) => branch = b);

            Assert.That(scheduler.Pending, Is.Not.Null, "원문 읽기가 스케줄러를 타지 않았다");
            Assert.That(branch, Is.Null, "일이 끝나기도 전에 화면을 그렸다");
            Assert.That(_busy.IsBusy, Is.True, "진행 표시 없이 셸이 멈춘 것처럼 보인다");
            Assert.That(_busy.IsCancellable, Is.False, "취소가 걸리지 않는 구간에 취소 버튼을 띄웠다");

            scheduler.RunPending();

            Assert.That(branch, Is.EqualTo("브랜치 원문"));
            Assert.That(_busy.IsBusy, Is.False);
        }

        [Test]
        public void LoadSelectedTexts_ReportsTheFailure_WhenReadingThrows()
        {
            // UI 스레드에서 그대로 터지면 셸이 함께 내려간다.
            var vm = NewViewModel(MappingMode.Deploy, out var repoPath);
            SelectOneDifference(vm, repoPath, "브랜치 원문");
            _smo.Setup(s => s.ScriptObjectToText(Server, Database, "dbo.P"))
                .Throws(new IOException("파일이 다른 프로세스에 잠겨 있습니다."));

            string? branch = null;
            Assert.DoesNotThrow(() => vm.LoadSelectedTexts((b, d) => branch = b));

            Assert.That(branch, Is.Empty, "실패했는데 이전 객체의 원문이 그대로 남는다");
            Assert.That(_busy.IsBusy, Is.False, "실패 뒤에도 진행 표시가 걸려 있다");
            Assert.That(_notifier.Errors, Is.Not.Empty);
        }

        /// <summary>
        /// 차이 하나를 목록에 넣고 고른다. 브랜치 파일도 함께 만든다.
        /// 비교를 돌리지 않는 이유는 그것도 스케줄러를 타기 때문이다 — 여기서 확인하려는 것은
        /// 원문 읽기가 스케줄러를 타는가이므로, 준비 과정이 같은 이음매를 쓰면 구분되지 않는다.
        /// </summary>
        private void SelectOneDifference(DeploymentViewModel vm, string repoPath, string branchFileText)
        {
            const string relativePath = "dbo/StoredProcedures/P.sql";
            Directory.CreateDirectory(Path.Combine(repoPath, "dbo", "StoredProcedures"));
            File.WriteAllText(Path.Combine(repoPath, "dbo", "StoredProcedures", "P.sql"), branchFileText);

            vm.Differences.Add(new DifferenceItemViewModel(
                new SchemaDifference("dbo.P", relativePath, "StoredProcedure", ObjectDiffState.Modified),
                MappingMode.Deploy));
            vm.SelectedDifference = vm.Differences[0];
        }

        /// <summary>작업을 붙들고 있다가 시켜야 돌린다 — "아직 끝나지 않은 동안"을 볼 수 있다.</summary>
        private sealed class DeferredScheduler : IBackgroundScheduler
        {
            public Action? Pending { get; private set; }

            public void Run<T>(Func<T> work, Action<T> onSucceeded, Action<Exception> onFailed)
            {
                Pending = () =>
                {
                    T value;
                    try { value = work(); }
                    catch (Exception ex) { onFailed(ex); return; }
                    onSucceeded(value);
                };
            }

            public void Post(Action action) => action();

            public void RunPending()
            {
                var pending = Pending;
                Pending = null;
                pending?.Invoke();
            }
        }
    }
}
