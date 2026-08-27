using System;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// mode는 실수를 막는 장치다. 판정이 두 곳에 생기면 화면과 Core가 갈라지고,
    /// 갈라진 쪽이 이기는 날 배포 클론에서 커밋이 나간다.
    /// </summary>
    [TestFixture]
    public class MappingPolicyTests
    {
        [TestCase(DbvcOperation.InstallTracker)]
        [TestCase(DbvcOperation.Extract)]
        [TestCase(DbvcOperation.Commit)]
        [TestCase(DbvcOperation.Push)]
        public void IsAllowed_ReturnsTrue_WhenModeIsWrite(DbvcOperation operation)
        {
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Write, operation), Is.True);
        }

        [TestCase(MappingMode.Deploy, DbvcOperation.InstallTracker)]
        [TestCase(MappingMode.Deploy, DbvcOperation.Extract)]
        [TestCase(MappingMode.Deploy, DbvcOperation.Commit)]
        [TestCase(MappingMode.Deploy, DbvcOperation.Push)]
        [TestCase(MappingMode.Audit, DbvcOperation.InstallTracker)]
        [TestCase(MappingMode.Audit, DbvcOperation.Extract)]
        [TestCase(MappingMode.Audit, DbvcOperation.Commit)]
        [TestCase(MappingMode.Audit, DbvcOperation.Push)]
        public void IsAllowed_ReturnsFalse_WhenModeIsNotWrite(MappingMode mode, DbvcOperation operation)
        {
            Assert.That(MappingPolicy.IsAllowed(mode, operation), Is.False);
        }

        [Test]
        public void IsAllowed_DeniesCompare_WhenModeIsWrite()
        {
            // 개발 DB는 정의상 master + 진행 중인 모든 feature 상태다.
            // 브랜치와의 차이 전체가 잡음이라 검사 자체가 의미를 갖지 않는다.
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Write, DbvcOperation.Compare), Is.False);
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Deploy, DbvcOperation.Compare), Is.True);
            Assert.That(MappingPolicy.IsAllowed(MappingMode.Audit, DbvcOperation.Compare), Is.True);
        }

        [TestCase(MappingMode.Write)]
        [TestCase(MappingMode.Deploy)]
        [TestCase(MappingMode.Audit)]
        public void IsAllowed_AllowsGenerateScript_InEveryMode(MappingMode mode)
        {
            // 결과물은 동작이 아니라 텍스트 파일이다. 막으면 안전이 늘지 않고 분기만 는다.
            Assert.That(MappingPolicy.IsAllowed(mode, DbvcOperation.GenerateScript), Is.True);
        }

        [Test]
        public void IsAllowed_Throws_WhenOperationIsUnknown()
        {
            // 새 동작이 생겼는데 표를 고치지 않으면 조용히 허용되는 것이 아니라 시끄럽게 죽어야 한다.
            Assert.Throws<InvalidOperationException>(
                () => MappingPolicy.IsAllowed(MappingMode.Write, (DbvcOperation)999));
        }

        [Test]
        public void BuildDeniedMessage_NamesBothModeAndOperationInKorean()
        {
            var message = MappingPolicy.BuildDeniedMessage(MappingMode.Audit, DbvcOperation.Commit);

            Assert.That(message, Does.Contain("감사"));
            Assert.That(message, Does.Contain("커밋"));
        }

        [Test]
        public void OperationNotAllowedException_CarriesTheDeniedMessage()
        {
            var ex = new OperationNotAllowedException(MappingMode.Deploy, DbvcOperation.Push);

            Assert.That(ex.Mode, Is.EqualTo(MappingMode.Deploy));
            Assert.That(ex.Operation, Is.EqualTo(DbvcOperation.Push));
            Assert.That(ex.Message, Is.EqualTo(MappingPolicy.BuildDeniedMessage(MappingMode.Deploy, DbvcOperation.Push)));
        }
    }
}
