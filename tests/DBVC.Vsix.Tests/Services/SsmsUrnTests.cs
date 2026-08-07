using DBVC.Vsix.Services;
using NUnit.Framework;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class SsmsUrnTests
    {
        [Test]
        public void TryGetDatabaseName_ReadsTheDatabaseNode()
        {
            const string urn = @"Server[@Name='LOCALHOST\SQL2022']/Database[@Name='AdventureWorks']";

            Assert.That(SsmsUrn.TryGetDatabaseName(urn), Is.EqualTo("AdventureWorks"));
        }

        [Test]
        public void TryGetDatabaseName_ReadsItFromDeeperNodes()
        {
            const string urn =
                @"Server[@Name='LOCALHOST\SQL2022']/Database[@Name='SalesDB']/Table[@Name='Person'and@Schema='dbo']";

            Assert.That(SsmsUrn.TryGetDatabaseName(urn), Is.EqualTo("SalesDB"));
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_ForAServerNode()
        {
            // 사용자가 데이터베이스를 지목하지 않았다는 뜻이다. 초기 카탈로그로 넘겨짚지 않는다.
            Assert.That(SsmsUrn.TryGetDatabaseName(@"Server[@Name='LOCALHOST\SQL2022']"), Is.Null);
        }

        [Test]
        public void TryGetDatabaseName_UnescapesDoubledQuotes()
        {
            // SMO URN은 값 안의 '를 ''로 이스케이프한다.
            const string urn = @"Server[@Name='S']/Database[@Name='Bob''s DB']/Table[@Name='T']";

            Assert.That(SsmsUrn.TryGetDatabaseName(urn), Is.EqualTo("Bob's DB"));
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_ForNullEmptyOrGarbage()
        {
            Assert.That(SsmsUrn.TryGetDatabaseName(null), Is.Null);
            Assert.That(SsmsUrn.TryGetDatabaseName(""), Is.Null);
            Assert.That(SsmsUrn.TryGetDatabaseName("not a urn at all"), Is.Null);
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_WhenTheQuoteIsNeverClosed()
        {
            Assert.That(SsmsUrn.TryGetDatabaseName("Server[@Name='S']/Database[@Name='unterminated"), Is.Null);
        }

        [Test]
        public void TryGetDatabaseName_ReturnsNull_WhenTheNameIsEmpty()
        {
            Assert.That(SsmsUrn.TryGetDatabaseName("Server[@Name='S']/Database[@Name='']"), Is.Null);
        }

        [Test]
        public void TryGetDatabaseName_ReturnsWhitespace_WhenTheNameIsWhitespaceOnly()
        {
            // 공백 하나는 length > 0이라 이 메서드 자체는 "값을 얻었다"고 본다. 공백만 있는
            // 이름을 대상으로 채택하면 안 된다는 판단은 이 메서드가 아니라 호출자(어댑터)의
            // IsNullOrWhiteSpace 관문이 맡는다 — 여기서는 반환값이 그대로 공백임을 고정한다.
            Assert.That(SsmsUrn.TryGetDatabaseName("Server[@Name='S']/Database[@Name=' ']"), Is.EqualTo(" "));
        }
    }
}
