using DBVC.Core;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class SessionPasswordCacheTests
    {
        [Test]
        public void TryGet_ReturnsNull_WhenNothingWasSet()
        {
            Assert.That(new SessionPasswordCache().TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Set_ThenTryGet_RoundTripsThePassword()
        {
            var cache = new SessionPasswordCache();

            cache.Set("srv", "db", "p@ss");

            Assert.That(cache.TryGet("srv", "db"), Is.EqualTo("p@ss"));
        }

        [Test]
        public void TryGet_IgnoresCase_LikeTheCredentialStore()
        {
            var cache = new SessionPasswordCache();
            cache.Set("SRV", "DB", "p@ss");

            Assert.That(cache.TryGet("srv", "db"), Is.EqualTo("p@ss"),
                "저장소와 키 규약이 다르면 같은 항목을 서로 다른 것으로 봅니다");
        }

        [Test]
        public void TryGet_KeepsDatabasesApart()
        {
            var cache = new SessionPasswordCache();
            cache.Set("srv", "db1", "one");
            cache.Set("srv", "db2", "two");

            Assert.That(cache.TryGet("srv", "db1"), Is.EqualTo("one"));
            Assert.That(cache.TryGet("srv", "db2"), Is.EqualTo("two"));
        }

        [Test]
        public void Set_RemovesTheEntry_WhenThePasswordIsNullOrEmpty()
        {
            var cache = new SessionPasswordCache();
            cache.Set("srv", "db", "p@ss");

            cache.Set("srv", "db", null);
            Assert.That(cache.TryGet("srv", "db"), Is.Null);

            cache.Set("srv", "db", "p@ss");
            cache.Set("srv", "db", "");
            Assert.That(cache.TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void Remove_ReportsWhetherSomethingWasThere()
        {
            var cache = new SessionPasswordCache();
            cache.Set("srv", "db", "p@ss");

            Assert.That(cache.Remove("srv", "db"), Is.True);
            Assert.That(cache.Remove("srv", "db"), Is.False);
            Assert.That(cache.TryGet("srv", "db"), Is.Null);
        }

        [Test]
        public void EmptyServerOrDatabase_IsIgnoredInsteadOfThrowing()
        {
            var cache = new SessionPasswordCache();

            Assert.DoesNotThrow(() => cache.Set("", "db", "p@ss"));
            Assert.That(cache.TryGet("", "db"), Is.Null);
            Assert.That(cache.Remove("srv", ""), Is.False);
        }
    }
}
