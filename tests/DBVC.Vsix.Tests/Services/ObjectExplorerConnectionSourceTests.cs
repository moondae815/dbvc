using DBVC.Vsix.Services;
using NUnit.Framework;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class ObjectExplorerConnectionSourceTests
    {
        // 이 테스트는 SSMS 셸 밖에서 돈다. 리플렉션 대상 어셈블리가 아예 로드되어 있지 않은,
        // 어댑터가 반드시 견뎌야 하는 환경이다. 실제 개체 탐색기 읽기는 계획서의 수동 검증이 담당한다.

        [Test]
        public void Constructor_DoesNotTouchTheShell()
        {
            Assert.DoesNotThrow(() => new ObjectExplorerConnectionSource());
        }

        [Test]
        public void TryGetCurrent_ReturnsNull_WhenTheShellIsNotThere()
        {
            Assert.That(new ObjectExplorerConnectionSource().TryGetCurrent(), Is.Null,
                "연결을 읽지 못하는 것과 도구 창이 죽는 것은 비교할 문제가 아닙니다");
        }

        [Test]
        public void TryGetSelectedUrn_ReturnsNull_WhenTheShellIsNotThere()
        {
            Assert.That(new ObjectExplorerConnectionSource().TryGetSelectedUrn(), Is.Null);
        }

        [Test]
        public void TryGetCurrent_CanBeCalledRepeatedly()
        {
            var source = new ObjectExplorerConnectionSource();

            Assert.DoesNotThrow(() =>
            {
                source.TryGetCurrent();
                source.TryGetCurrent();
            });
        }
    }
}
