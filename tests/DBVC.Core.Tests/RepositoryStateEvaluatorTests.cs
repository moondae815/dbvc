using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// DBVC는 저장소의 유일한 주인이 아니다. 외부 Git 클라이언트가 브랜치를 바꾸거나
    /// 병합을 중간에 남겨 둔 저장소를 열게 되고, 그 상태에서 비교하면 조용히 틀린 결과가 나온다.
    /// </summary>
    [TestFixture]
    public class RepositoryStateEvaluatorTests
    {
        [Test]
        public void Evaluate_ReturnsNone_WhenBranchMatches()
        {
            var reason = RepositoryStateEvaluator.Evaluate("master", false, null, "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));
        }

        [Test]
        public void Evaluate_ReturnsNone_WhenExpectedBranchIsEmpty()
        {
            // 개발 클론은 브랜치를 자유롭게 전환한다. 고정이 없으면 어느 브랜치든 정상이다.
            // IsNullOrWhiteSpace를 쓰므로 null, 빈 문자열, 공백 모두 "미고정"이다.
            var reason = RepositoryStateEvaluator.Evaluate("feature/x", false, null, null);
            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));

            reason = RepositoryStateEvaluator.Evaluate("feature/x", false, null, "");
            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));

            reason = RepositoryStateEvaluator.Evaluate("feature/x", false, null, "   ");
            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));
        }

        [Test]
        public void Evaluate_ReturnsBranchMismatch_WhenBranchDiffers()
        {
            var reason = RepositoryStateEvaluator.Evaluate("develop", false, null, "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.BranchMismatch));
        }

        [Test]
        public void Evaluate_IgnoresCase_WhenComparingBranch()
        {
            var reason = RepositoryStateEvaluator.Evaluate("Master", false, null, "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.None));
        }

        [Test]
        public void Evaluate_ReturnsDetachedHead_EvenWhenNoBranchIsExpected()
        {
            // 고정이 없어도 detached는 막는다 - 커밋해도 어느 브랜치에도 남지 않는다.
            var reason = RepositoryStateEvaluator.Evaluate(null, true, null, null);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.DetachedHead));
        }

        [Test]
        public void Evaluate_PrefersOperationInProgress_OverBranchMismatch()
        {
            // 병합 중이면 브랜치 이름이 맞아도 작업 트리가 중간 상태다. 그쪽을 먼저 알려야
            // 사용자가 "브랜치를 바꾸면 되겠구나"로 오해하지 않는다.
            var reason = RepositoryStateEvaluator.Evaluate("develop", false, "Merge", "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.OperationInProgress));
        }

        [Test]
        public void Evaluate_PrefersOperationInProgress_OverDetachedHead()
        {
            // 병합 중이고 detached이면 우선순위는 병합을 먼저 알린다.
            var reason = RepositoryStateEvaluator.Evaluate(null, true, "Rebase", null);

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.OperationInProgress));
        }

        [Test]
        public void Evaluate_PrefersDetachedHead_OverBranchMismatch()
        {
            // detached이고 브랜치도 맞지 않으면 detached를 먼저 알린다.
            var reason = RepositoryStateEvaluator.Evaluate(null, true, null, "master");

            Assert.That(reason, Is.EqualTo(RepositoryBlockReason.DetachedHead));
        }

        [Test]
        public void BuildMessage_NamesBothBranches_WhenBranchMismatch()
        {
            var message = RepositoryStateEvaluator.BuildMessage(
                RepositoryBlockReason.BranchMismatch, "develop", "master", null);

            Assert.That(message, Does.Contain("develop"));
            Assert.That(message, Does.Contain("master"));
        }

        [Test]
        public void BuildMessage_ReturnsNull_WhenNotBlocked()
        {
            var message = RepositoryStateEvaluator.BuildMessage(
                RepositoryBlockReason.None, "master", "master", null);

            Assert.That(message, Is.Null);
        }

        [Test]
        public void BuildMessage_InterpolatesOperationName_WhenOperationInProgress()
        {
            var message = RepositoryStateEvaluator.BuildMessage(
                RepositoryBlockReason.OperationInProgress, null, null, "Merge");

            Assert.That(message, Does.Contain("Merge"));
            Assert.That(message, Does.Not.Contain("detached"));
            Assert.That(message, Does.Not.Contain("브랜치"));
        }

        [Test]
        public void BuildMessage_ReturnsDetachedMessage_WhenDetachedHead()
        {
            var message = RepositoryStateEvaluator.BuildMessage(
                RepositoryBlockReason.DetachedHead, null, null, null);

            Assert.That(message, Does.Contain("detached"));
            Assert.That(message, Is.Not.Null);
            Assert.That(message, Does.Not.Contain("작업"));
        }
    }
}
