using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    [TestFixture]
    public class ObjectHistoryViewModelTests
    {
        private const string Server = "LocalServer";
        private const string Database = "SalesDB";
        private const string RelativePath = "dbo/Tables/Users.sql";

        private Mock<IGitManager> _git = null!;

        [SetUp]
        public void SetUp()
        {
            _git = new Mock<IGitManager>();
            _git.Setup(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new List<CommitInfo>());
        }

        private ObjectHistoryViewModel NewViewModel() => new ObjectHistoryViewModel(_git.Object);

        private static CommitInfo Commit(string sha, string message, string author = "Tester")
            => new CommitInfo
            {
                Sha = sha,
                Message = message,
                Author = author,
                Date = new DateTimeOffset(2026, 8, 1, 14, 30, 0, TimeSpan.Zero)
            };

        private void GivenHistory(params CommitInfo[] commits)
        {
            _git.Setup(g => g.GetHistory(Server, Database, RelativePath)).Returns(commits.ToList());
        }

        /// <summary>선택된 객체가 없을 때 GitManager가 돌려줄 저장소 전체 이력.</summary>
        private void GivenRepositoryHistory(params CommitInfo[] commits)
        {
            _git.Setup(g => g.GetHistory(Server, Database, It.Is<string?>(p => string.IsNullOrWhiteSpace(p))))
                .Returns(commits.ToList());
        }

        // ---------- 변환 ----------

        [Test]
        public void Load_PreservesTheFullShaAndMapsParentSha()
        {
            const string fullSha = "a3f9c2b1d4e5f60718293a4b5c6d7e8f90123456";
            const string parentSha = "1111222233334444555566667777888899990000";
            GivenHistory(new CommitInfo
            {
                Sha = fullSha,
                ParentSha = parentSha,
                Message = "인덱스 추가",
                Author = "Tester",
                Date = new DateTimeOffset(2026, 8, 1, 14, 30, 0, TimeSpan.Zero)
            });
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            var entry = vm.Entries.Single();
            Assert.That(entry.Sha, Is.EqualTo(fullSha));
            Assert.That(entry.ParentSha, Is.EqualTo(parentSha));
            Assert.That(entry.HasParent, Is.True);
            Assert.That(entry.ShortSha, Is.EqualTo("a3f9c2b"));
        }

        [Test]
        public void HistoryEntryViewModel_HasParent_IsFalse_WhenParentShaIsNullOrEmpty()
        {
            var withNull = new HistoryEntryViewModel { ParentSha = null };
            var withEmpty = new HistoryEntryViewModel { ParentSha = string.Empty };
            var withParent = new HistoryEntryViewModel { ParentSha = "parent123" };

            Assert.That(withNull.HasParent, Is.False);
            Assert.That(withEmpty.HasParent, Is.False);
            Assert.That(withParent.HasParent, Is.True);
        }

        [Test]
        public void Load_ShortensTheShaToSevenCharacters()
        {
            GivenHistory(Commit("a3f9c2b1d4e5f60718293a4b5c6d7e8f90123456", "인덱스 추가"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().ShortSha, Is.EqualTo("a3f9c2b"));
        }

        [Test]
        public void Load_KeepsAShaShorterThanSevenCharactersAsIs()
        {
            GivenHistory(Commit("abc12", "짧은 해시"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().ShortSha, Is.EqualTo("abc12"));
        }

        [Test]
        public void Load_KeepsAShaOfExactlySevenCharactersAsIs()
        {
            GivenHistory(Commit("abc1234", "경계값"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().ShortSha, Is.EqualTo("abc1234"));
        }

        [Test]
        public void Load_ShowsOnlyTheFirstLineOfTheCommitMessage()
        {
            GivenHistory(Commit("abc1234567", "제목 줄\n\n본문 설명이 이어진다"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().Message, Is.EqualTo("제목 줄"),
                "목록 한 행에 여러 줄이 들어가면 표가 무너집니다");
        }

        [Test]
        public void Load_ShowsOnlyTheFirstLineOfTheCommitMessage_WhenLineEndingsAreCrlf()
        {
            GivenHistory(Commit("abc1234567", "제목 줄\r\n\r\n본문 설명이 이어진다"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().Message, Is.EqualTo("제목 줄"),
                "Windows에서 만든 커밋은 CRLF 줄바꿈을 사용합니다");
        }

        [Test]
        public void Load_FormatsTheDate()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().Date, Is.EqualTo("2026-08-01 14:30"));
        }

        [Test]
        public void Load_KeepsTheOrderGitReturned()
        {
            GivenHistory(
                Commit("1111111111", "최신"),
                Commit("2222222222", "이전"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Select(e => e.Message), Is.EqualTo(new[] { "최신", "이전" }),
                "GitManager.GetHistory가 최신순으로 주므로 그대로 보여줍니다");
        }

        // ---------- 목록 상태 ----------

        [Test]
        public void Load_ReplacesThePreviousEntries()
        {
            GivenHistory(Commit("1111111111", "첫 조회"));
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);

            GivenHistory(Commit("2222222222", "두 번째 조회"));
            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Select(e => e.Message), Is.EqualTo(new[] { "두 번째 조회" }),
                "다른 객체를 선택했을 때 이전 객체의 이력이 남으면 안 됩니다");
        }

        [Test]
        public void IsEmpty_IsTrue_BeforeAnyLoad()
        {
            Assert.That(NewViewModel().IsEmpty, Is.True);
        }

        [Test]
        public void IsEmpty_IsFalse_WhenHistoryExists()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.IsEmpty, Is.False);
        }

        [Test]
        public void IsEmpty_RaisesPropertyChanged_OnLoad()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.Load(Server, Database, RelativePath);

            Assert.That(raised, Does.Contain(nameof(ObjectHistoryViewModel.IsEmpty)),
                "안내 문구의 표시 여부가 이 알림에 걸려 있습니다");
        }

        // ---------- 인자 검증 ----------

        [TestCase(null, Database, RelativePath)]
        [TestCase(Server, null, RelativePath)]
        public void Load_DoesNotQueryGit_WhenTheTargetIsMissing(string? server, string? database, string? path)
        {
            var vm = NewViewModel();

            vm.Load(server, database, path);

            Assert.That(vm.Entries, Is.Empty);
            _git.Verify(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ---------- 저장소 전체 이력 ----------

        /// <summary>
        /// 커밋 직후에는 변경 목록이 비어 선택할 객체가 없다. 그때도 방금 만든 커밋이 보여야 한다.
        /// </summary>
        [TestCase(null)]
        [TestCase("   ")]
        public void Load_ShowsTheWholeRepositoryHistory_WhenNoObjectIsGiven(string? path)
        {
            GivenRepositoryHistory(Commit("aaa1111222", "초기 스키마 스냅샷"), Commit("bbb3333444", "Initial commit"));
            var vm = NewViewModel();

            vm.Load(Server, Database, path);

            Assert.That(vm.Entries.Select(e => e.ShortSha), Is.EqualTo(new[] { "aaa1111", "bbb3333" }));
        }

        [Test]
        public void Load_FallsBackToTheRepositoryHistory_WhenTheSelectionGoesAway()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            GivenRepositoryHistory(Commit("def7654321", "저장소 커밋"));
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);

            vm.Load(Server, Database, null);

            Assert.That(vm.Entries.Single().ShortSha, Is.EqualTo("def7654"));
        }

        // ---------- 범위 표시 ----------

        [Test]
        public void ScopeLabel_SaysWholeRepository_WhenNoObjectIsGiven()
        {
            var vm = NewViewModel();

            vm.Load(Server, Database, null);

            Assert.That(vm.ScopeLabel, Is.EqualTo("저장소 전체"));
        }

        [Test]
        public void ScopeLabel_NamesTheObject_WhenAnObjectIsGiven()
        {
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.ScopeLabel, Is.EqualTo("dbo.Users"),
                "경로가 아니라 사용자가 아는 객체 이름으로 보여야 합니다");
        }

        [Test]
        public void ScopeLabel_IsEmpty_WhenThereIsNoTarget()
        {
            var vm = NewViewModel();

            vm.Load(null, null, null);

            Assert.That(vm.ScopeLabel, Is.Empty);
        }

        // ---------- Diff 모델 생성 및 선택 상태 ----------

        [Test]
        public void SelectedEntry_SetsSelectedDiffModel()
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, "abcdef1")).Returns("old");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, RelativePath, "abcdef1")).Returns("new");

            bool raised = false;
            vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ObjectHistoryViewModel.SelectedDiffModel)) raised = true; };

            vm.SelectedEntry = entry;

            Assert.That(raised, Is.True, "PropertyChanged for SelectedDiffModel should be raised");
            Assert.That(vm.SelectedDiffModel, Is.Not.Null);
            Assert.That(vm.IsDiffVisible, Is.True);
        }

        [Test]
        public void SelectedEntry_PassesFullShaToGitManager_WhenShaIsAvailable()
        {
            var vm = NewViewModel();
            const string fullSha = "a3f9c2b1d4e5f60718293a4b5c6d7e8f90123456";
            var entry = new HistoryEntryViewModel
            {
                Sha = fullSha,
                ShortSha = "a3f9c2b"
            };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, fullSha)).Returns("old");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, RelativePath, fullSha)).Returns("new");

            vm.SelectedEntry = entry;

            _git.Verify(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, fullSha), Times.Once);
            _git.Verify(g => g.GetFileContentAtCommit(Server, Database, RelativePath, fullSha), Times.Once);
        }

        [Test]
        public void SelectedEntry_WhenSetToNull_ClearsSelectedDiffModelAndIsDiffVisible()
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, "abcdef1")).Returns("old");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, RelativePath, "abcdef1")).Returns("new");

            vm.SelectedEntry = entry;
            Assert.That(vm.SelectedDiffModel, Is.Not.Null);

            vm.SelectedEntry = null;

            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
        }

        [TestCase(null, Database, RelativePath)]
        [TestCase(Server, null, RelativePath)]
        [TestCase(Server, Database, null)]
        public void SelectedEntry_WhenContextIsMissing_SetsSelectedDiffModelToNull(string? server, string? database, string? path)
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
            vm.ServerName = server;
            vm.DatabaseName = database;
            vm.RelativePath = path;

            vm.SelectedEntry = entry;

            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
            _git.Verify(g => g.GetFileContentAtCommit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void SelectedEntry_RaisesPropertyChanged_ForSelectedEntry_And_SelectedDiffModel_And_IsDiffVisible()
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, "abcdef1")).Returns("old");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, RelativePath, "abcdef1")).Returns("new");

            var propertyChanges = new List<string?>();
            vm.PropertyChanged += (s, e) => propertyChanges.Add(e.PropertyName);

            vm.SelectedEntry = entry;

            Assert.That(propertyChanges, Does.Contain(nameof(ObjectHistoryViewModel.SelectedEntry)));
            Assert.That(propertyChanges, Does.Contain(nameof(ObjectHistoryViewModel.SelectedDiffModel)));
            Assert.That(propertyChanges, Does.Contain(nameof(ObjectHistoryViewModel.IsDiffVisible)));
        }

        [Test]
        public void Load_SetsContextProperties_And_ResetsSelectedEntry()
        {
            GivenHistory(Commit("abcdef1234", "커밋1"));
            var vm = NewViewModel();
            vm.ServerName = "OldServer";
            vm.DatabaseName = "OldDB";
            vm.RelativePath = "OldPath.sql";
            vm.SelectedEntry = new HistoryEntryViewModel { ShortSha = "oldsha1" };

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.ServerName, Is.EqualTo(Server));
            Assert.That(vm.DatabaseName, Is.EqualTo(Database));
            Assert.That(vm.RelativePath, Is.EqualTo(RelativePath));
            Assert.That(vm.SelectedEntry, Is.Null);
            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
        }

        [Test]
        public void SelectedEntry_WhenCommitHasNoParent_HandlesNullParentContentGracefully()
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "initsha" };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            // 최초 커밋의 경우 부모가 없으므로 GitManager는 "" 또는 null을 반환
            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, "initsha")).Returns((string?)null);
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, RelativePath, "initsha")).Returns("create table Users (id int);");

            vm.SelectedEntry = entry;

            Assert.That(vm.SelectedDiffModel, Is.Not.Null);
            Assert.That(vm.IsDiffVisible, Is.True);
        }

        // ---------- 전체 이력 모드에서의 변경 파일 목록 및 Diff ----------

        [Test]
        public void IsSingleObjectMode_IsTrueWhenRelativePathIsSet_AndFalseWhenNull()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);
            Assert.That(vm.IsSingleObjectMode, Is.True);

            vm.Load(Server, Database, null);
            Assert.That(vm.IsSingleObjectMode, Is.False);
        }

        [Test]
        public void SelectedEntry_WhenInGlobalHistoryMode_PopulatesChangedFiles()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);
            Assert.That(vm.IsSingleObjectMode, Is.False);

            const string commitSha = "c1234567890abcdef";
            var changed = new List<HistoryChangedFile>
            {
                new HistoryChangedFile { RelativePath = "dbo/Tables/Users.sql", State = HistoryChangedFileState.Added },
                new HistoryChangedFile { RelativePath = "dbo/StoredProcedures/usp_GetUsers.sql", State = HistoryChangedFileState.Modified }
            };
            _git.Setup(g => g.GetChangedFilesAtCommit(Server, Database, commitSha)).Returns(changed);

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "c123456" };

            Assert.That(vm.ChangedFiles.Count, Is.EqualTo(2));
            Assert.That(vm.ChangedFiles[0].ObjectName, Is.EqualTo("dbo.Users"));
            Assert.That(vm.ChangedFiles[0].ObjectType, Is.EqualTo("Table"));
            Assert.That(vm.ChangedFiles[0].ObjectTypeText, Is.EqualTo("Table"));
            Assert.That(vm.ChangedFiles[0].StateText, Is.EqualTo("추가"));
            Assert.That(vm.ChangedFiles[0].RelativePath, Is.EqualTo("dbo/Tables/Users.sql"));

            Assert.That(vm.ChangedFiles[1].ObjectName, Is.EqualTo("dbo.usp_GetUsers"));
            Assert.That(vm.ChangedFiles[1].ObjectType, Is.EqualTo("StoredProcedure"));
            Assert.That(vm.ChangedFiles[1].ObjectTypeText, Is.EqualTo("SP"));
            Assert.That(vm.ChangedFiles[1].StateText, Is.EqualTo("수정"));
            Assert.That(vm.ChangedFiles[1].RelativePath, Is.EqualTo("dbo/StoredProcedures/usp_GetUsers.sql"));

            Assert.That(vm.SelectedChangedFile, Is.Null);
            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
        }

        [Test]
        public void SelectedEntry_WhenInSingleObjectMode_DoesNotPopulateChangedFiles()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);
            Assert.That(vm.IsSingleObjectMode, Is.True);

            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, RelativePath, "c123456")).Returns("old");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, RelativePath, "c123456")).Returns("new");

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "c123456", ShortSha = "c123456" };

            Assert.That(vm.ChangedFiles, Is.Empty);
            _git.Verify(g => g.GetChangedFilesAtCommit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(vm.SelectedDiffModel, Is.Not.Null);
            Assert.That(vm.IsDiffVisible, Is.True);
        }

        [Test]
        public void SelectedChangedFile_WhenSelectedInGlobalMode_UpdatesSelectedDiffModel()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            const string commitSha = "c1234567890abcdef";
            var changed = new List<HistoryChangedFile>
            {
                new HistoryChangedFile { RelativePath = "dbo/Tables/Users.sql", State = HistoryChangedFileState.Modified }
            };
            _git.Setup(g => g.GetChangedFilesAtCommit(Server, Database, commitSha)).Returns(changed);
            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, "dbo/Tables/Users.sql", commitSha)).Returns("create table Users (id int);");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, "dbo/Tables/Users.sql", commitSha)).Returns("create table Users (id int, name nvarchar(50));");

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "c123456" };
            Assert.That(vm.SelectedDiffModel, Is.Null);

            bool diffModelChanged = false;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ObjectHistoryViewModel.SelectedDiffModel))
                    diffModelChanged = true;
            };

            vm.SelectedChangedFile = vm.ChangedFiles[0];

            Assert.That(diffModelChanged, Is.True);
            Assert.That(vm.SelectedDiffModel, Is.Not.Null);
            Assert.That(vm.IsDiffVisible, Is.True);
            _git.Verify(g => g.GetFileContentAtCommitParent(Server, Database, "dbo/Tables/Users.sql", commitSha), Times.Once);
            _git.Verify(g => g.GetFileContentAtCommit(Server, Database, "dbo/Tables/Users.sql", commitSha), Times.Once);
        }

        [Test]
        public void SelectedChangedFile_WhenSetToNull_ClearsSelectedDiffModel()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            const string commitSha = "c1234567890abcdef";
            var changed = new List<HistoryChangedFile>
            {
                new HistoryChangedFile { RelativePath = "dbo/Tables/Users.sql", State = HistoryChangedFileState.Modified }
            };
            _git.Setup(g => g.GetChangedFilesAtCommit(Server, Database, commitSha)).Returns(changed);
            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, "dbo/Tables/Users.sql", commitSha)).Returns("old");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, "dbo/Tables/Users.sql", commitSha)).Returns("new");

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "c123456" };
            vm.SelectedChangedFile = vm.ChangedFiles[0];
            Assert.That(vm.SelectedDiffModel, Is.Not.Null);

            vm.SelectedChangedFile = null;

            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
        }

        [Test]
        public void SelectedEntry_WhenChangedToAnotherCommitInGlobalMode_ResetsSelectedChangedFileAndSelectedDiffModel()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            const string commit1 = "1111111111111111";
            const string commit2 = "2222222222222222";
            _git.Setup(g => g.GetChangedFilesAtCommit(Server, Database, commit1)).Returns(new List<HistoryChangedFile>
            {
                new HistoryChangedFile { RelativePath = "dbo/Tables/Users.sql", State = HistoryChangedFileState.Modified }
            });
            _git.Setup(g => g.GetChangedFilesAtCommit(Server, Database, commit2)).Returns(new List<HistoryChangedFile>
            {
                new HistoryChangedFile { RelativePath = "dbo/Views/vw_Report.sql", State = HistoryChangedFileState.Added }
            });
            _git.Setup(g => g.GetFileContentAtCommitParent(Server, Database, "dbo/Tables/Users.sql", commit1)).Returns("old");
            _git.Setup(g => g.GetFileContentAtCommit(Server, Database, "dbo/Tables/Users.sql", commit1)).Returns("new");

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commit1, ShortSha = "1111111" };
            vm.SelectedChangedFile = vm.ChangedFiles[0];
            Assert.That(vm.SelectedDiffModel, Is.Not.Null);

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commit2, ShortSha = "2222222" };

            Assert.That(vm.SelectedChangedFile, Is.Null);
            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
            Assert.That(vm.ChangedFiles.Count, Is.EqualTo(1));
            Assert.That(vm.ChangedFiles[0].ObjectName, Is.EqualTo("dbo.vw_Report"));
        }

        [Test]
        public void SelectedEntry_WhenSetToNull_ClearsChangedFilesAndSelectedChangedFile()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            const string commitSha = "c1234567890abcdef";
            _git.Setup(g => g.GetChangedFilesAtCommit(Server, Database, commitSha)).Returns(new List<HistoryChangedFile>
            {
                new HistoryChangedFile { RelativePath = "dbo/Tables/Users.sql", State = HistoryChangedFileState.Modified }
            });

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "c123456" };
            vm.SelectedChangedFile = vm.ChangedFiles[0];

            vm.SelectedEntry = null;

            Assert.That(vm.ChangedFiles, Is.Empty);
            Assert.That(vm.SelectedChangedFile, Is.Null);
            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
        }
    }
}
