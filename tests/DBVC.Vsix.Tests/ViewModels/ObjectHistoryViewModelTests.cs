using System;
using System.Collections.Generic;
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
            // 실제 GitManager.GetCommitDetail은 실패해도 null이 아니라 빈 CommitDetail을 준다.
            // 모의 객체 기본값(null)을 그대로 두면 특정 커밋/경로를 세팅하지 않은 테스트가 NRE로 깨진다.
            _git.Setup(g => g.GetCommitDetail(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new CommitDetail());
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

        private static HistoryChangedFile ChangedFile(string relativePath, HistoryChangedFileState state = HistoryChangedFileState.Modified)
            => new HistoryChangedFile { RelativePath = relativePath, State = state };

        private static CommitDetail Detail(params HistoryChangedFile[] files)
            => new CommitDetail { ChangedFiles = files.ToList(), TotalChangedFileCount = files.Length };

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

            _git.Setup(g => g.GetCommitDetail(Server, Database, "abcdef1", RelativePath))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });

            bool raised = false;
            vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ObjectHistoryViewModel.SelectedDiffModel)) raised = true; };

            vm.SelectedEntry = entry;

            Assert.That(raised, Is.True, "PropertyChanged for SelectedDiffModel should be raised");
            Assert.That(vm.SelectedDiffModel, Is.Not.Null);
            Assert.That(vm.IsDiffVisible, Is.True);
        }

        [Test]
        public void GetSelectedFileTexts_ReturnsNull_BeforeAnySuccessfulRead()
        {
            var vm = NewViewModel();

            Assert.That(vm.GetSelectedFileTexts(), Is.Null);
        }

        [Test]
        public void GetSelectedFileTexts_ReturnsRawTexts_AfterSuccessfulRead()
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            _git.Setup(g => g.GetCommitDetail(Server, Database, "abcdef1", RelativePath))
                .Returns(new CommitDetail { OldText = "old\r\n", NewText = "new\r\n" });

            vm.SelectedEntry = entry;

            Assert.That(vm.GetSelectedFileTexts(), Is.EqualTo(("old\r\n", "new\r\n")));
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

            _git.Setup(g => g.GetCommitDetail(Server, Database, fullSha, RelativePath))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });

            vm.SelectedEntry = entry;

            _git.Verify(g => g.GetCommitDetail(Server, Database, fullSha, RelativePath), Times.Once);
        }

        [Test]
        public void SelectedEntry_WhenSetToNull_ClearsSelectedDiffModelAndIsDiffVisible()
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            _git.Setup(g => g.GetCommitDetail(Server, Database, "abcdef1", RelativePath))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });

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
            // 세 경우 중 (Server, Database, null)은 전체 이력 모드라 변경 파일 목록 조회는 정당하다.
            // 여기서 지켜야 할 것은 Diff 조회(경로가 있는 호출)가 일어나지 않는다는 것뿐이다.
            _git.Verify(g => g.GetCommitDetail(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.Is<string>(p => p != null)), Times.Never);
        }

        [Test]
        public void SelectedEntry_RaisesPropertyChanged_ForSelectedEntry_And_SelectedDiffModel_And_IsDiffVisible()
        {
            var vm = NewViewModel();
            var entry = new HistoryEntryViewModel { ShortSha = "abcdef1" };
            vm.ServerName = Server;
            vm.DatabaseName = Database;
            vm.RelativePath = RelativePath;

            _git.Setup(g => g.GetCommitDetail(Server, Database, "abcdef1", RelativePath))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });

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

            // 최초 커밋의 경우 부모가 없으므로 GitManager는 OldText를 null로 채운다
            _git.Setup(g => g.GetCommitDetail(Server, Database, "initsha", RelativePath))
                .Returns(new CommitDetail { OldText = null, NewText = "create table Users (id int);" });

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
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(new CommitDetail { ChangedFiles = changed, TotalChangedFileCount = changed.Count });

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

            _git.Setup(g => g.GetCommitDetail(Server, Database, "c123456", RelativePath))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "c123456", ShortSha = "c123456" };

            Assert.That(vm.ChangedFiles, Is.Empty);
            _git.Verify(g => g.GetCommitDetail(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null), Times.Never);
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
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(new CommitDetail { ChangedFiles = changed, TotalChangedFileCount = changed.Count });
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, "dbo/Tables/Users.sql"))
                .Returns(new CommitDetail { OldText = "create table Users (id int);", NewText = "create table Users (id int, name nvarchar(50));" });

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
            _git.Verify(g => g.GetCommitDetail(Server, Database, commitSha, "dbo/Tables/Users.sql"), Times.Once);
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
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(new CommitDetail { ChangedFiles = changed, TotalChangedFileCount = changed.Count });
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, "dbo/Tables/Users.sql"))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });

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
            _git.Setup(g => g.GetCommitDetail(Server, Database, commit1, null))
                .Returns(Detail(ChangedFile("dbo/Tables/Users.sql")));
            _git.Setup(g => g.GetCommitDetail(Server, Database, commit2, null))
                .Returns(Detail(ChangedFile("dbo/Views/vw_Report.sql", HistoryChangedFileState.Added)));
            _git.Setup(g => g.GetCommitDetail(Server, Database, commit1, "dbo/Tables/Users.sql"))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });

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
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(Detail(ChangedFile("dbo/Tables/Users.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "c123456" };
            vm.SelectedChangedFile = vm.ChangedFiles[0];

            vm.SelectedEntry = null;

            Assert.That(vm.ChangedFiles, Is.Empty);
            Assert.That(vm.SelectedChangedFile, Is.Null);
            Assert.That(vm.SelectedDiffModel, Is.Null);
            Assert.That(vm.IsDiffVisible, Is.False);
        }

        // ---------- 백그라운드 스케줄러와 stale 가드 ----------

        [Test]
        public void SelectedEntry_IgnoresTheEarlierRequest_WhenItFinishesLast()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(Detail(ChangedFile("dbo/Tables/A.sql")));
            _git.Setup(g => g.GetCommitDetail(Server, Database, "bbb", null))
                .Returns(Detail(ChangedFile("dbo/Tables/B.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };
            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "bbb", ShortSha = "bbb" };

            // 나중 요청(bbb)을 먼저, 앞선 요청(aaa)을 나중에 흘린다.
            scheduler.FlushAt(1);
            scheduler.FlushAt(0);

            Assert.That(vm.ChangedFiles.Count, Is.EqualTo(1));
            Assert.That(vm.ChangedFiles[0].RelativePath, Is.EqualTo("dbo/Tables/B.sql"));
        }

        /// <summary>
        /// 위 테스트는 전체 이력 모드라 변경 파일 목록 요청만 큐에 쌓이고 Diff 요청은 아예
        /// 나가지 않는다(선택된 변경 파일이 없어 UpdateDiffModel이 조기 종료한다) - 그래서
        /// _diffToken 쪽 stale 가드는 검증되지 않는다. 단일 객체 모드로 두면 매 SelectedEntry마다
        /// UpdateDiffModel이 실제로 Diff 요청을 큐에 넣으므로, 여기서만 _diffToken을 확인할 수 있다.
        /// </summary>
        [Test]
        public void SelectedEntry_IgnoresTheEarlierDiffRequest_WhenItFinishesLast()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, RelativePath);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", RelativePath))
                .Returns(new CommitDetail { OldText = "old-a", NewText = "new-a" });
            _git.Setup(g => g.GetCommitDetail(Server, Database, "bbb", RelativePath))
                .Returns(new CommitDetail { OldText = "old-b", NewText = "new-b" });

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };
            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "bbb", ShortSha = "bbb" };

            // 단일 객체 모드에서는 LoadChangedFiles가 즉시 반환하므로(IsSingleObjectMode) 두 선택 모두
            // UpdateDiffModel의 Diff 요청만 큐에 쌓인다 - 이 값이 2가 아니면 이 테스트는 아무것도 증명하지 못한다.
            Assert.That(scheduler.PendingCount, Is.EqualTo(2));

            // 나중 요청(bbb)을 먼저, 앞선 요청(aaa)을 나중에 흘린다.
            scheduler.FlushAt(1);
            scheduler.FlushAt(0);

            Assert.That(vm.SelectedDiffModel, Is.Not.Null);
            Assert.That(vm.SelectedDiffModel!.NewText.Lines.Select(l => l.Text), Does.Contain("new-b"),
                "앞선 요청(aaa)이 늦게 끝나 나중 요청(bbb)의 결과를 덮어쓰면 안 된다");
            Assert.That(vm.SelectedDiffModel!.NewText.Lines.Select(l => l.Text), Does.Not.Contain("new-a"));

            // GetSelectedFileTexts()도 SelectedDiffModel과 같은 표(stale 검사)를 지나야 한다 - 원본
            // 대입이 stale 검사보다 앞에 있으면 늦게 끝난 aaa가 여기서만 조용히 이겨 이 값이 old-a/new-a로
            // 되돌아간다.
            Assert.That(vm.GetSelectedFileTexts(), Is.EqualTo(("old-b", "new-b")));
        }

        /// <summary>
        /// 커밋과 변경 파일을 고른 채로 Diff 요청이 아직 진행 중일 때 Load가 다시 불리는 경우다
        /// ("전체 이력으로" 버튼, 개체 탐색기 우클릭 등). Load의 SelectedEntry = null이 타는
        /// UpdateDiffModel의 조기 반환 분기가 표(_diffToken)를 올리지 않으면, 진행 중이던 요청이
        /// 나중에 흘러도 stale 검사를 통과해 방금 비운 SelectedDiffModel 위에 옛 커밋의 Diff를
        /// 되살린다. 표를 올리지 않고 되돌리면 이 테스트는 실패한다.
        /// </summary>
        [Test]
        public void Load_InvalidatesADiffRequest_ThatWasStillInFlight()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, null);

            const string commitSha = "aaa1111111111111";
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(Detail(ChangedFile("dbo/Tables/Users.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "aaa1111" };
            scheduler.FlushAll(); // 변경 파일 목록 요청을 흘려 ChangedFiles를 채운다.

            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, "dbo/Tables/Users.sql"))
                .Returns(new CommitDetail { OldText = "old", NewText = "new" });
            vm.SelectedChangedFile = vm.ChangedFiles[0]; // Diff 요청을 큐에 넣지만 아직 흘리지 않는다.
            Assert.That(scheduler.PendingCount, Is.EqualTo(1));

            // Diff 요청이 아직 진행 중인 채로 다른 대상의 이력을 연다.
            vm.Load(Server, Database, "dbo/Tables/Other.sql");
            Assert.That(scheduler.PendingCount, Is.EqualTo(1), "Load 자체는 새 요청을 내보내지 않아야 한다");

            // 진행 중이던 옛 요청을 흘린다.
            scheduler.FlushAll();

            Assert.That(vm.SelectedDiffModel, Is.Null,
                "Load 이후에는 그 전에 나간 Diff 요청의 결과가 화면에 반영되면 안 된다");
        }

        /// <summary>
        /// 위 Diff 테스트의 변경 파일 목록 판이다. 목록 요청이 진행 중일 때 필터 모드로
        /// 옮겨 가면 LoadChangedFiles가 IsSingleObjectMode로 조기 반환하는데, 그 자리가 표
        /// (_changedFilesToken)를 올리지 않으면 진행 중이던 요청이 stale 검사를 통과해 방금 비운
        /// 목록을 옆 대상의 파일로 다시 채운다. 표를 올리지 않고 되돌리면 이 테스트는 실패한다.
        /// </summary>
        [Test]
        public void Load_InvalidatesAChangedFilesRequest_ThatWasStillInFlight()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, null);

            const string commitSha = "aaa1111111111111";
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(Detail(ChangedFile("dbo/Tables/Users.sql")));

            // 병합 커밋으로 둔다 - 아니면 늦게 끝난 콜백이 안내까지 되살리는지를 볼 수 없다.
            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "aaa1111", ParentCount = 2 };
            Assert.That(scheduler.PendingCount, Is.EqualTo(1), "사전 조건: 변경 파일 목록 요청 하나만 떠 있다");

            // 목록 요청이 아직 진행 중인 채로 다른 객체로 좁힌 이력을 연다.
            vm.Load(Server, Database, "dbo/Tables/Other.sql");
            Assert.That(vm.ChangedFiles, Is.Empty, "사전 조건: Load가 목록을 비웠다");
            Assert.That(scheduler.PendingCount, Is.EqualTo(1), "Load 자체는 새 요청을 내보내지 않아야 한다");

            scheduler.FlushAll();

            Assert.That(vm.ChangedFiles, Is.Empty,
                "Load 이후에는 그 전에 나간 변경 파일 목록 요청의 결과가 화면에 반영되면 안 된다");
            Assert.That(vm.HasHistoryNotice, Is.False,
                "안내도 같은 표를 지나야 한다 - 목록만 비어 두면 없는 목록을 설명하는 문구가 남는다");
        }

        /// <summary>
        /// 같은 조기 반환 분기의 다른 조건(_selectedEntry == null)이다. 이력 목록에서
        /// 선택을 푸는 경로라 모드는 그대로고 선택만 사라진다.
        /// </summary>
        [Test]
        public void SelectedEntry_InvalidatesAChangedFilesRequest_WhenClearedWhileStillInFlight()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, null);

            const string commitSha = "bbb2222222222222";
            _git.Setup(g => g.GetCommitDetail(Server, Database, commitSha, null))
                .Returns(Detail(ChangedFile("dbo/Tables/Users.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = commitSha, ShortSha = "bbb2222", ParentCount = 2 };
            Assert.That(scheduler.PendingCount, Is.EqualTo(1), "사전 조건: 변경 파일 목록 요청 하나만 떠 있다");

            vm.SelectedEntry = null;
            Assert.That(vm.ChangedFiles, Is.Empty, "사전 조건: setter가 목록을 비웠다");

            scheduler.FlushAll();

            Assert.That(vm.ChangedFiles, Is.Empty,
                "선택을 푸는 순간 진행 중이던 요청이 그 목록을 되살리면 안 된다");
            Assert.That(vm.HasHistoryNotice, Is.False);
        }

        [Test]
        public void SelectedEntry_RunsTheGitReadThroughTheScheduler()
        {
            var scheduler = new DeferredBackgroundScheduler();
            var vm = new ObjectHistoryViewModel(_git.Object, new DiffService(), scheduler);
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(Detail(ChangedFile("dbo/Tables/A.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };

            Assert.That(vm.ChangedFiles, Is.Empty, "콜백을 흘리기 전에는 컬렉션이 비어 있어야 한다");
            Assert.That(scheduler.PendingCount, Is.EqualTo(1));

            scheduler.FlushAll();
            Assert.That(vm.ChangedFiles.Count, Is.EqualTo(1));
        }

        [Test]
        public void SelectedEntry_BuildsAMergeNotice_WhenTheCommitHasTwoParents()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(Detail(ChangedFile("dbo/Tables/A.sql")));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa", ParentCount = 2 };

            Assert.That(vm.HasHistoryNotice, Is.True);
            Assert.That(vm.HistoryNotice, Does.Contain("병합 커밋"));
        }

        /// <summary>
        /// 안내가 머리글로 올라갔으므로 필터 모드에서도 보인다. 필터 이력에도 병합 커밋은
        /// 남을 수 있다 - GetHistory가 쓰는 경로 단순화는 파일이 어느 부모와도 다른 병합만
        /// 남기는데, 그게 바로 첫 부모 기준이라는 사실이 가장 필요한 커밋이다.
        /// </summary>
        [Test]
        public void SelectedEntry_BuildsAMergeNotice_InSingleObjectMode()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa", ParentCount = 2 };

            Assert.That(vm.HasHistoryNotice, Is.True);
            Assert.That(vm.HistoryNotice, Does.Contain("병합 커밋"));

            // 안내 하나 때문에 변경 파일 목록 조회를 되살리면 안 된다 - ParentCount는 엔트리에
            // 이미 있다. 필터 모드에서 목록을 읽지 않는다는 규약은 그대로다.
            _git.Verify(
                g => g.GetCommitDetail(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null),
                Times.Never);
        }

        [Test]
        public void SelectedEntry_LeavesTheNoticeEmpty_InSingleObjectModeForAPlainCommit()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa", ParentCount = 1 };

            Assert.That(vm.HasHistoryNotice, Is.False);
        }

        [Test]
        public void SelectedEntry_BuildsATruncationNotice_WhenTheFileListIsCut()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Returns(new CommitDetail
                {
                    ChangedFiles = new List<HistoryChangedFile> { ChangedFile("dbo/Tables/A.sql") },
                    TotalChangedFileCount = 900,
                    IsTruncated = true
                });

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };

            Assert.That(vm.HistoryNotice, Does.Contain("900"));
        }

        [Test]
        public void SelectedEntry_SetsAKoreanNotice_WhenLoadingChangedFilesFails()
        {
            var vm = NewViewModel();
            vm.Load(Server, Database, null);

            _git.Setup(g => g.GetCommitDetail(Server, Database, "aaa", null))
                .Throws(new InvalidOperationException("repository read failed"));

            vm.SelectedEntry = new HistoryEntryViewModel { Sha = "aaa", ShortSha = "aaa" };

            Assert.That(vm.HasHistoryNotice, Is.True);
            Assert.That(vm.HistoryNotice, Is.EqualTo("변경된 파일 목록을 읽지 못했습니다."),
                "빈 목록과 읽기 실패를 화면에서 구분할 수 있어야 한다");
        }

        [Test]
        public void HistoryEntryViewModel_MergeMark_IsSetOnlyWhenParentCountExceedsOne()
        {
            var merge = HistoryEntryViewModel.From(new CommitInfo { Sha = "a", ParentCount = 2 });
            var plain = HistoryEntryViewModel.From(new CommitInfo { Sha = "b", ParentCount = 1 });

            Assert.That(merge.MergeMark, Is.EqualTo("병합"));
            Assert.That(plain.MergeMark, Is.Empty);
        }
    }
}
