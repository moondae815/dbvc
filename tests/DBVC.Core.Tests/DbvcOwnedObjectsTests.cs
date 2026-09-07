using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 이름을 하나씩 보태는 방식을 접두사 규칙으로 바꾼 판정. 설치 스크립트의
    /// LIKE N'DBVC[_]%'와 같은 결과를 내야 하며, 어긋나면 도구가 자기 객체를
    /// 저장소에 커밋하거나 사용자 객체를 조용히 추적에서 뺀다.
    /// </summary>
    [TestFixture]
    public class DbvcOwnedObjectsTests
    {
        [Test]
        public void IsOwned_ReturnsTrue_WhenNameStartsWithDbvcPrefix()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DbvcOwnedObjects.IsOwned("DBVC_ChangeLog"), Is.True);
                Assert.That(DbvcOwnedObjects.IsOwned("DBVC_PurgeChangeLog"), Is.True);
            });
        }

        [Test]
        public void IsOwned_ReturnsTrue_WhenNameIsTheDdlTrigger()
        {
            // 트리거만 접두사를 따르지 않는다. 이름을 바꾸면 기존 설치와 어긋난다.
            Assert.That(DbvcOwnedObjects.IsOwned("trg_DBVC_DDL_Tracker"), Is.True);
        }

        [Test]
        public void IsOwned_IsCaseInsensitive()
        {
            // 데이터 정렬이 대소문자를 구분하는 서버에서도 판정은 같아야 한다.
            Assert.That(DbvcOwnedObjects.IsOwned("dbvc_changelog"), Is.True);
        }

        [Test]
        public void IsOwned_ReturnsFalse_WhenPrefixIsNotFollowedByUnderscore()
        {
            // SQL 쪽 LIKE의 [_] 이스케이프와 같은 판정이다. 밑줄을 와일드카드로 두면
            // 사용자의 DBVCx 객체까지 추적에서 빠진다.
            Assert.Multiple(() =>
            {
                Assert.That(DbvcOwnedObjects.IsOwned("DBVCx_Table"), Is.False);
                Assert.That(DbvcOwnedObjects.IsOwned("DBVCReport"), Is.False);
            });
        }

        [Test]
        public void IsOwned_ReturnsFalse_WhenNameIsNullOrBlank()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DbvcOwnedObjects.IsOwned(null), Is.False);
                Assert.That(DbvcOwnedObjects.IsOwned("   "), Is.False);
            });
        }
    }
}
