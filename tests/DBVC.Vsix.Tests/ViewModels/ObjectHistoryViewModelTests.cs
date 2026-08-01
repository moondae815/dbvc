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

        // ---------- 변환 ----------

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
        public void Load_ShowsOnlyTheFirstLineOfTheCommitMessage()
        {
            GivenHistory(Commit("abc1234567", "제목 줄\n\n본문 설명이 이어진다"));
            var vm = NewViewModel();

            vm.Load(Server, Database, RelativePath);

            Assert.That(vm.Entries.Single().Message, Is.EqualTo("제목 줄"),
                "목록 한 행에 여러 줄이 들어가면 표가 무너집니다");
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
        [TestCase(Server, Database, null)]
        [TestCase(Server, Database, "   ")]
        public void Load_DoesNotQueryGit_WhenAnArgumentIsMissing(string? server, string? database, string? path)
        {
            var vm = NewViewModel();

            vm.Load(server, database, path);

            Assert.That(vm.Entries, Is.Empty);
            _git.Verify(g => g.GetHistory(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void Load_ClearsTheList_WhenTheSelectionGoesAway()
        {
            GivenHistory(Commit("abc1234567", "변경"));
            var vm = NewViewModel();
            vm.Load(Server, Database, RelativePath);

            vm.Load(Server, Database, null);

            Assert.That(vm.Entries, Is.Empty);
            Assert.That(vm.IsEmpty, Is.True);
        }
    }
}
