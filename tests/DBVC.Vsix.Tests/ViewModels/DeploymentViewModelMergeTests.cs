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
                _notifier, new RecordingSaveDialog(), new InlineBackgroundScheduler(), new BusyState());
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
            _git.Verify(g => g.MergeAndPush(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void MergeCommand_OffersComparison_WhenMerged()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1"))
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
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1"))
                .Returns(MergeOutcome.Of(kind, "사유: " + expected));

            vm.MergeCommand.Execute(null);

            Assert.That(_notifier.ErrorCalls.Single().Message, Does.Contain(expected));
            _smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void MergeCommand_ShowsError_WhenCoreThrows()
        {
            var vm = LoadedWith(MappingMode.Deploy, "develop", new MergePreview { ChangedPaths = new[] { "a.sql" } }, Branch("PROJ-1"));
            _git.Setup(g => g.MergeAndPush(Server, Database, "PROJ-1")).Throws(new GitRemoteException("원격에 연결하지 못했습니다."));

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
    }
}
