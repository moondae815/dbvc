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
    /// 병합과 배포 사이가 끊기면 "병합했으니 나갔겠지"가 생긴다. 병합 뒤 차이 검사를 제안하는 것이 요점이다.
    /// </summary>
    [TestFixture]
    public class DeploymentViewModelMergeTests
    {
        private const string Server = "TestServer";
        private const string Database = "TestDb";

        private Mock<IConfigManager> _config = null!;
        private Mock<IGitManager> _git = null!;
        private Mock<ISmoManager> _smo = null!;
        private RecordingNotifier _notifier = null!;

        private DeploymentViewModel NewViewModel(MappingMode mode, string branch)
        {
            return NewViewModel(mode, branch, new InlineBackgroundScheduler());
        }

        private DeploymentViewModel NewViewModel(MappingMode mode, string branch, IBackgroundScheduler scheduler)
        {
            var mapping = new MappingConfig
            {
                ServerName = Server, DatabaseName = Database, GitPath = Path.GetTempPath(), Mode = mode, Branch = branch
            };
            _config = new Mock<IConfigManager>();
            _config.Setup(c => c.TryGetMapping(Server, Database)).Returns(mapping);
            _git = new Mock<IGitManager>();
            _git.Setup(g => g.PullChanges(Server, Database)).Returns(PullResult.AlreadyUpToDate);
            _smo = new Mock<ISmoManager>();
            _notifier = new RecordingNotifier();

            var vm = new DeploymentViewModel(
                _config.Object, _git.Object, _smo.Object,
                new ScriptExporter(_config.Object, _git.Object),
                _notifier, new RecordingSaveDialog(), scheduler, new BusyState());
            vm.SetTarget(Server, Database, mode);
            return vm;
        }

        private static UnmergedBranch Branch(string name, bool? inDevelop = null) => new UnmergedBranch
        {
            Name = name, LastCommitAuthor = "김개발", LastCommitTime = new DateTimeOffset(2026, 9, 17, 10, 30, 0, TimeSpan.FromHours(9)),
            CommitCount = 2, IsInDevelop = inDevelop
        };

        private DeploymentViewModel LoadedWith(MappingMode mode, string target, MergePreview preview, params UnmergedBranch[] branches)
        {
            var vm = NewViewModel(mode, target);
            _git.Setup(g => g.GetUnmergedBranches(Server, Database)).Returns(branches);
            _git.Setup(g => g.PreviewMerge(Server, Database, It.IsAny<string>())).Returns(preview);
            vm.LoadBranchesCommand.Execute(null);
            vm.SelectedBranch = vm.UnmergedBranches.First();
            return vm;
        }

        [Test]
        public void LoadBranchesCommand_FillsTheList_WithKoreanColumns()
        {
            var vm = NewViewModel(MappingMode.Audit, "master");
            _git.Setup(g => g.GetUnmergedBranches(Server, Database)).Returns(new[] { Branch("PROJ-1", inDevelop: true) });

            vm.LoadBranchesCommand.Execute(null);

            var item = vm.UnmergedBranches.Single();
            Assert.That(item.Name, Is.EqualTo("PROJ-1"));
            Assert.That(item.InDevelopText, Is.EqualTo("반영됨"));
            Assert.That(item.CommitCountText, Is.EqualTo("2개"));
            Assert.That(vm.IsTargetMaster, Is.True);
        }

        [Test]
        public void SelectedBranch_ShowsPreview_WhenBranchIsChosen()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop",
                new MergePreview { ChangedPaths = new[] { "dbo/StoredProcedures/usp_Order.sql" } }, Branch("PROJ-1"));

            Assert.That(vm.PreviewText, Does.Contain("dbo/StoredProcedures/usp_Order.sql"));
            Assert.That(vm.CanMerge, Is.True);
        }

        [Test]
        public void CanMerge_IsFalse_WhenPreviewHasConflicts()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop",
                new MergePreview { ConflictPaths = new[] { "dbo/Views/v_A.sql" } }, Branch("PROJ-1"));

            Assert.That(vm.CanMerge, Is.False);
            Assert.That(vm.MergeCommand.CanExecute(null), Is.False);
            Assert.That(vm.PreviewText, Does.Contain("충돌").And.Contain("dbo/Views/v_A.sql"));
            Assert.That(vm.PreviewText, Does.Contain("develop을 티켓 브랜치로 병합해서 풀면 안 됩니다"));
        }

        [Test]
        public void MergeCommand_IsDisabled_InWriteMode()
        {
            var vm = LoadedWith(MappingMode.Write, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));

            Assert.That(vm.MergeCommand.CanExecute(null), Is.False);
        }

        [Test]
        public void MergeCommand_IncludesLeaksInConfirmation_WhenTargetIsMaster()
        {
            var preview = new MergePreview
            {
                ChangedPaths = new[] { "dbo/StoredProcedures/usp_Order.sql" },
                Leaks = new[] { new PromotionLeak { Path = "dbo/StoredProcedures/usp_Order.sql", BranchName = "PROJ-120", Lines = new[] { "DiscountRate DECIMAL(5,2)" } } }
            };
            var vm = LoadedWith(MappingMode.Audit, "master", preview, Branch("PROJ-1"));
            _notifier.ConfirmResult = false;

            vm.MergeCommand.Execute(null);

            Assert.That(_notifier.ConfirmCalls.Single().Message,
                Does.Contain("PROJ-1").And.Contain("master").And.Contain("PROJ-120").And.Contain("DiscountRate DECIMAL(5,2)"));
            _git.Verify(g => g.MergeAndPush(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void MergeCommand_OffersComparison_WhenMerged()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1", It.IsAny<string>()))
                .Returns(MergeOutcome.Of(MergeOutcomeKind.Merged, null, new[] { "a.sql" }));
            _smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(new ComparisonResult { ComparedCount = 1 });

            vm.MergeCommand.Execute(null);   // 첫 Confirm: 병합, 둘째 Confirm: 차이 검사

            Assert.That(_notifier.ConfirmCalls, Has.Count.EqualTo(2));
            Assert.That(_notifier.ConfirmCalls[1].Message, Does.Contain("PROJ-1을 develop에 병합하고 올렸습니다"));
            _smo.Verify(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(vm.UnmergedBranches.Select(b => b.Name), Does.Not.Contain("PROJ-1"));
        }

        [TestCase(MergeOutcomeKind.PushRejected, "다시 시도하세요")]
        [TestCase(MergeOutcomeKind.LocalAhead, "원격에 없는 커밋")]
        [TestCase(MergeOutcomeKind.Refused, "사유")]
        public void MergeCommand_ShowsCoreMessage_WhenNotMerged(MergeOutcomeKind kind, string expected)
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1", It.IsAny<string>()))
                .Returns(MergeOutcome.Of(kind, "사유: " + expected));

            vm.MergeCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls.Single().Message, Does.Contain(expected));
            _smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void MergeCommand_ShowsError_WhenCoreThrows()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1", It.IsAny<string>())).Throws(new GitRemoteException("원격에 연결하지 못했습니다."));

            vm.MergeCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls.Single().Title, Is.EqualTo("DBVC 병합 실패"));
        }

        [Test]
        public void SetTarget_ClearsMergeState_WhenTargetChanges()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));

            vm.SetTarget("Other", "Db", MappingMode.Deploy);

            Assert.That(vm.UnmergedBranches, Is.Empty);
            Assert.That(vm.SelectedBranch, Is.Null);
            Assert.That(vm.PreviewText, Is.Null);
        }

        [Test]
        public void MergeCommand_PassesPreviewedSourceSha()
        {
            // 미리보기 뒤 올라온 커밋을 병합하지 않게 Core가 거부할 근거다.
            var vm = LoadedWith(MappingMode.Deploy, "develop",
                new MergePreview { ChangedPaths = new[] { "a.sql" }, SourceSha = "abc123" }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1", It.IsAny<string>()))
                .Returns(MergeOutcome.Of(MergeOutcomeKind.Refused, "사유"));

            vm.MergeCommand.Execute(null);

            _git.Verify(g => g.MergeAndPush(Server, Database, "PROJ-1", "abc123"), Times.Once);
        }

        [Test]
        public void LoadBranchesCommand_DropsLateResult_WhenTargetChanged()
        {
            // 이전 대상의 브랜치를 새 대상의 목록으로 보여 주면 다른 대상에 병합하는 사고가 된다.
            var scheduler = new DeferredBackgroundScheduler();
            var vm = NewViewModel(MappingMode.Deploy, "develop", scheduler);
            _git.Setup(g => g.GetUnmergedBranches(Server, Database)).Returns(new[] { Branch("PROJ-1") });

            vm.LoadBranchesCommand.Execute(null);
            vm.SetTarget("Other", "Db", MappingMode.Deploy);
            scheduler.FlushAll();

            Assert.That(vm.UnmergedBranches, Is.Empty);
            Assert.That(vm.PreviewText, Is.Null);
            Assert.That(vm.Busy.IsBusy, Is.False);
        }

        [Test]
        public void MergeCommand_DoesNotOfferComparison_WhenTargetChangedDuringMerge()
        {
            // 병합 결과가 도착했을 때 차이 검사를 제안하면 바뀐 대상(운영일 수 있다)을 검사하게 된다.
            var scheduler = new DeferredBackgroundScheduler();
            var vm = NewViewModel(MappingMode.Deploy, "develop", scheduler);
            _git.Setup(g => g.GetUnmergedBranches(Server, Database)).Returns(new[] { Branch("PROJ-1") });
            _git.Setup(g => g.PreviewMerge(Server, Database, "PROJ-1"))
                .Returns(new MergePreview { ChangedPaths = new[] { "a.sql" }, SourceSha = "abc123" });
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1", It.IsAny<string>()))
                .Returns(MergeOutcome.Of(MergeOutcomeKind.Merged, null, new[] { "a.sql" }));
            vm.LoadBranchesCommand.Execute(null);
            scheduler.FlushAll();
            vm.SelectedBranch = vm.UnmergedBranches.First();
            scheduler.FlushAll();

            vm.MergeCommand.Execute(null);                     // 병합 확인 뒤 결과가 대기열에 걸린다
            vm.SetTarget("Other", "Db", MappingMode.Deploy);   // 결과가 오기 전에 대상이 바뀐다
            scheduler.FlushAll();

            Assert.That(_notifier.ConfirmCalls, Has.Count.EqualTo(1), "병합 확인 외에 차이 검사를 묻지 않아야 합니다");
            _smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(_notifier.InfoCalls.Single().Message, Does.Contain(Server + "." + Database).And.Contain("PROJ-1"),
                "원격에 올라간 병합은 어느 대상의 것인지 밝혀 알립니다");
            Assert.That(vm.Busy.IsBusy, Is.False);
        }

        /// <summary>
        /// 선택을 지운 것과 다음 선택이 없는 것은 같은 사건이다 - 둘 다 "지금 뜬 미리보기는
        /// 무효"라는 뜻이어야 한다. 세대를 선택이 있을 때만 올리면, 지우기 직전에 날아간
        /// 요청의 응답이 나중에 도착해 지운 화면을 도로 채운다.
        /// </summary>
        [Test]
        public void SelectedBranch_IgnoresLatePreview_WhenSelectionIsCleared()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = NewViewModel(MappingMode.Deploy, "develop", scheduler);
            _git.Setup(g => g.GetUnmergedBranches(Server, Database)).Returns(new[] { Branch("PROJ-1") });
            _git.Setup(g => g.PreviewMerge(Server, Database, "PROJ-1"))
                .Returns(new MergePreview { ChangedPaths = new[] { "a.sql" } });

            vm.LoadBranchesCommand.Execute(null);
            scheduler.FlushAll();

            vm.SelectedBranch = vm.UnmergedBranches.First();   // PreviewMerge 요청이 대기열에 걸린다
            vm.SelectedBranch = null;                          // 응답이 오기 전에 선택을 지운다

            scheduler.FlushAll();                              // 늦게 도착한 응답을 흘려보낸다

            Assert.That(vm.PreviewText, Is.Null);
        }
    }
}
