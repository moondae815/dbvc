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

        [Test]
        public void TryParseObjectIdentity_ValidTableUrn_ReturnsTrueAndExtractsParts()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']/Table[@Name='Person' and @Schema='dbo']";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.That(result, Is.True);
            Assert.That(db, Is.EqualTo("SalesDB"));
            Assert.That(schema, Is.EqualTo("dbo"));
            Assert.That(type, Is.EqualTo("Table"));
            Assert.That(name, Is.EqualTo("Person"));
        }

        [Test]
        public void TryParseObjectIdentity_ValidTableUrnWithoutSpaces_ReturnsTrueAndExtractsParts()
        {
            var urn = "Server[@Name='HOST\\INST']/Database[@Name='SalesDB']/Table[@Name='Person'and@Schema='dbo']";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.That(result, Is.True);
            Assert.That(db, Is.EqualTo("SalesDB"));
            Assert.That(schema, Is.EqualTo("dbo"));
            Assert.That(type, Is.EqualTo("Table"));
            Assert.That(name, Is.EqualTo("Person"));
        }

        [Test]
        public void TryParseObjectIdentity_SchemaBeforeName_ReturnsTrueAndExtractsParts()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']/StoredProcedure[@Schema='dbo' and @Name='usp_GetCustomer']";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.That(result, Is.True);
            Assert.That(db, Is.EqualTo("SalesDB"));
            Assert.That(schema, Is.EqualTo("dbo"));
            Assert.That(type, Is.EqualTo("StoredProcedure"));
            Assert.That(name, Is.EqualTo("usp_GetCustomer"));
        }

        [Test]
        public void TryParseObjectIdentity_ObjectWithoutSchema_ReturnsTrueWithNullSchema()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']/DatabaseRole[@Name='db_owner']";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.That(result, Is.True);
            Assert.That(db, Is.EqualTo("SalesDB"));
            Assert.That(schema, Is.Null);
            Assert.That(type, Is.EqualTo("DatabaseRole"));
            Assert.That(name, Is.EqualTo("db_owner"));
        }

        [Test]
        public void TryParseObjectIdentity_UnescapesDoubledQuotesInNameAndSchema()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']/Table[@Name='Bob''s Table' and @Schema='my''schema']";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.That(result, Is.True);
            Assert.That(db, Is.EqualTo("SalesDB"));
            Assert.That(schema, Is.EqualTo("my'schema"));
            Assert.That(type, Is.EqualTo("Table"));
            Assert.That(name, Is.EqualTo("Bob's Table"));
        }

        [Test]
        public void TryParseObjectIdentity_InvalidUrn_ReturnsFalse()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']/Tables";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.That(result, Is.False);
            Assert.That(db, Is.Null);
            Assert.That(schema, Is.Null);
            Assert.That(type, Is.Null);
            Assert.That(name, Is.Null);
        }

        [Test]
        public void TryParseObjectIdentity_DatabaseNode_ReturnsFalse()
        {
            var urn = "Server[@Name='HOST']/Database[@Name='SalesDB']";
            bool result = SsmsUrn.TryParseObjectIdentity(urn, out var db, out var schema, out var type, out var name);

            Assert.That(result, Is.False);
            Assert.That(db, Is.Null);
            Assert.That(schema, Is.Null);
            Assert.That(type, Is.Null);
            Assert.That(name, Is.Null);
        }

        [Test]
        public void TryParseObjectIdentity_NullOrEmptyOrGarbage_ReturnsFalse()
        {
            Assert.That(SsmsUrn.TryParseObjectIdentity(null, out _, out _, out _, out _), Is.False);
            Assert.That(SsmsUrn.TryParseObjectIdentity("", out _, out _, out _, out _), Is.False);
            Assert.That(SsmsUrn.TryParseObjectIdentity("invalid urn string", out _, out _, out _, out _), Is.False);
        }
    }
}
