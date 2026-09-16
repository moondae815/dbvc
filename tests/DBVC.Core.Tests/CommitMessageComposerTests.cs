using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class CommitMessageComposerCleanTests
    {
        [Test]
        public void Clean_ReturnsMessage_WhenAlreadyWellFormed()
        {
            Assert.That(
                CommitMessageComposer.Clean("feat: 주문 조회 프로시저에 취소일자 필터를 더한다"),
                Is.EqualTo("feat: 주문 조회 프로시저에 취소일자 필터를 더한다"));
        }

        [Test]
        public void Clean_RemovesCodeFence_WhenResponseIsWrapped()
        {
            var raw = "```\nfix: 재고 계산의 반올림 오류를 고친다\n```";

            Assert.That(CommitMessageComposer.Clean(raw), Is.EqualTo("fix: 재고 계산의 반올림 오류를 고친다"));
        }

        [Test]
        public void Clean_KeepsFirstLineOnly_WhenResponseHasBody()
        {
            var raw = "feat: 회원 등급 뷰를 더한다\n\n- 등급 산출 기준을 바꾼다\n- 인덱스를 더한다";

            Assert.That(CommitMessageComposer.Clean(raw), Is.EqualTo("feat: 회원 등급 뷰를 더한다"));
        }

        [Test]
        public void Clean_RemovesSurroundingQuotes_WhenResponseIsQuoted()
        {
            Assert.That(
                CommitMessageComposer.Clean("\"chore: 사용하지 않는 프로시저를 지운다\""),
                Is.EqualTo("chore: 사용하지 않는 프로시저를 지운다"));
        }

        [Test]
        public void Clean_AddsChorePrefix_WhenPrefixMissing()
        {
            // 형식 위반은 드물어도 0이 되지 않는다. 접두어 없는 메시지가 이력에 남지 않게 한다.
            Assert.That(
                CommitMessageComposer.Clean("주문 테이블에 컬럼을 더한다"),
                Is.EqualTo("chore: 주문 테이블에 컬럼을 더한다"));
        }

        [Test]
        public void Clean_AddsChorePrefix_WhenPrefixIsNotAllowed()
        {
            // style은 이 저장소가 쓰는 집합에 없다.
            Assert.That(
                CommitMessageComposer.Clean("style: 들여쓰기를 고친다"),
                Is.EqualTo("chore: style: 들여쓰기를 고친다"));
        }

        [Test]
        public void Clean_KeepsScopedPrefix_WhenModelAddsScope()
        {
            // 스코프를 쓰지 말라고 했는데도 붙는 경우가 있다. 형식으로는 유효하므로 그대로 둔다.
            Assert.That(
                CommitMessageComposer.Clean("feat(dbo): 주문 뷰를 더한다"),
                Is.EqualTo("feat(dbo): 주문 뷰를 더한다"));
        }

        [Test]
        public void Clean_TruncatesTo72Characters_WhenMessageTooLong()
        {
            var raw = "feat: " + new string('가', 100);

            var cleaned = CommitMessageComposer.Clean(raw);

            Assert.That(cleaned.Length, Is.EqualTo(72));
        }

        [Test]
        public void Clean_ReturnsEmpty_WhenResponseIsEmpty()
        {
            // 호출자가 "빈 응답"을 실패로 다룰 수 있어야 한다. 여기서 문장을 지어내지 않는다.
            Assert.That(CommitMessageComposer.Clean("   "), Is.Empty);
        }

        [Test]
        public void Clean_RemovesTrailingPeriod_WhenMessageEndsWithPeriod()
        {
            // 형식이 마침표 없음을 요구하는데, 정제 로직이 그걸 지우는 유일한 자리다.
            Assert.That(
                CommitMessageComposer.Clean("feat: 주문 뷰를 더한다."),
                Is.EqualTo("feat: 주문 뷰를 더한다"));
        }

        [Test]
        public void Clean_SelectsLineWithAllowedPrefix_WhenResponseHasPreamble()
        {
            // 모델이 안내문을 앞에 붙이는 경우가 있다 — 안내문을 chore로 감싸면 진짜 메시지가 사라진다.
            var raw = "다음은 커밋 메시지입니다:\nfeat: 주문 뷰를 더한다";

            Assert.That(CommitMessageComposer.Clean(raw), Is.EqualTo("feat: 주문 뷰를 더한다"));
        }

        [Test]
        public void Clean_RemovesNestedWrappers_WhenResponseIsQuotedTwice()
        {
            // 감싸기를 한 번만 벗기면 바깥 인용부호 안에 있던 안쪽 인용부호가 그대로 남는다.
            var raw = "'\"feat: 주문 뷰를 더한다\"'";

            Assert.That(CommitMessageComposer.Clean(raw), Is.EqualTo("feat: 주문 뷰를 더한다"));
        }
    }

    [TestFixture]
    public class CommitMessageComposerPromptTests
    {
        private static DiffFileChange Change(string path, string status, string patch) =>
            new DiffFileChange { RelativePath = path, Status = status, Patch = patch };

        [Test]
        public void BuildUserMessage_ListsObjectNameAndState_WhenChangeGiven()
        {
            var changes = new[] { Change("dbo/Procedures/usp_GetOrder.sql", "Modified", "@@ -1 +1 @@\n-old\n+new") };

            var message = CommitMessageComposer.BuildUserMessage(changes, maxDiffLines: 400);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("dbo.usp_GetOrder"));
                Assert.That(message, Does.Contain("수정"));
            });
        }

        [Test]
        public void BuildUserMessage_UsesRelativePath_WhenPathIsNotConventional()
        {
            // 규약 밖 경로를 조용히 버리면 AI가 변경 하나를 통째로 못 본다.
            var changes = new[] { Change("weird.txt", "Added", "@@ -0 +1 @@\n+x") };

            Assert.That(CommitMessageComposer.BuildUserMessage(changes, 400), Does.Contain("weird.txt"));
        }

        [Test]
        public void BuildUserMessage_ShowsPlaceholder_WhenPathIsBlank()
        {
            // 빈 경로를 그대로 두면 목록 줄이 "-  (수정)"처럼 비어 그 객체를 식별할 수 없게 된다.
            var changes = new[] { Change("   ", "Modified", "@@ -1 +1 @@\n-old\n+new") };

            Assert.That(CommitMessageComposer.BuildUserMessage(changes, 400), Does.Contain("알 수 없는 경로"));
        }

        [Test]
        public void BuildUserMessage_TranslatesStates_WhenAddedOrDeleted()
        {
            var changes = new[]
            {
                Change("dbo/Tables/Orders.sql", "Added", "@@ -0 +1 @@\n+a"),
                Change("dbo/Views/v_Old.sql", "Deleted", "@@ -1 +0 @@\n-b"),
            };

            var message = CommitMessageComposer.BuildUserMessage(changes, 400);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("추가"));
                Assert.That(message, Does.Contain("삭제"));
            });
        }

        [Test]
        public void BuildUserMessage_TruncatesPatch_WhenDiffExceedsLimit()
        {
            var longPatch = string.Join("\n", Enumerable.Range(0, 100).Select(i => "+line" + i));
            var changes = new[] { Change("dbo/Tables/Orders.sql", "Modified", longPatch) };

            var message = CommitMessageComposer.BuildUserMessage(changes, maxDiffLines: 10);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("-- (이하 생략)"));
                Assert.That(message, Does.Not.Contain("+line99"));
            });
        }

        [Test]
        public void BuildUserMessage_KeepsEveryObjectInList_WhenDiffTruncated()
        {
            // 잘린 diff로도 "무엇이 바뀌었는지"는 말할 수 있어야 한다.
            var longPatch = string.Join("\n", Enumerable.Range(0, 100).Select(i => "+line" + i));
            var changes = new[]
            {
                Change("dbo/Tables/Orders.sql", "Modified", longPatch),
                Change("dbo/Procedures/usp_GetOrder.sql", "Modified", longPatch),
            };

            var message = CommitMessageComposer.BuildUserMessage(changes, maxDiffLines: 6);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("dbo.Orders"));
                Assert.That(message, Does.Contain("dbo.usp_GetOrder"));
            });
        }

        [Test]
        public void SystemPrompt_StatesFormatRules_Always()
        {
            Assert.Multiple(() =>
            {
                Assert.That(CommitMessageComposer.SystemPrompt, Does.Contain("feat"));
                Assert.That(CommitMessageComposer.SystemPrompt, Does.Contain("72"));
            });
        }
    }
}
