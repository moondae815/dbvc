using NUnit.Framework;
using DBVC.Core;

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
    }
}
