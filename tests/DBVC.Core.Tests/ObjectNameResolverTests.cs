using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class ObjectNameResolverTests
    {
        // ---------- 식별자 파싱 ----------

        [Test]
        public void TryParse_ParsesSchemaQualifiedName()
        {
            Assert.That(ObjectNameResolver.TryParse("dbo.Users", out var schema, out var name), Is.True);
            Assert.That(schema, Is.EqualTo("dbo"));
            Assert.That(name, Is.EqualTo("Users"));
        }

        [Test]
        public void TryParse_StripsSquareBrackets()
        {
            Assert.That(ObjectNameResolver.TryParse("[dbo].[Users]", out var schema, out var name), Is.True);
            Assert.That(schema, Is.EqualTo("dbo"));
            Assert.That(name, Is.EqualTo("Users"));
        }

        [Test]
        public void TryParse_LeavesSchemaUnset_ForBareObjectName()
        {
            Assert.That(ObjectNameResolver.TryParse("usp_GetUsers", out var schema, out var name), Is.True);
            Assert.That(schema, Is.Null, "스키마를 임의로 dbo라고 단정하면 다른 스키마의 동명 객체를 놓친다");
            Assert.That(name, Is.EqualTo("usp_GetUsers"));
        }

        [Test]
        public void TryParse_UsesTheLastTwoParts_OfAFullyQualifiedName()
        {
            Assert.That(ObjectNameResolver.TryParse("MyServer.SalesDB.sales.Orders", out var schema, out var name), Is.True);
            Assert.That(schema, Is.EqualTo("sales"));
            Assert.That(name, Is.EqualTo("Orders"));
        }

        [Test]
        [TestCase("dbo.Users;")]
        [TestCase("  dbo.Users  ")]
        [TestCase("dbo.Users,")]
        [TestCase("(dbo.Users)")]
        public void TryParse_TrimsSurroundingWhitespaceAndPunctuation(string selection)
        {
            Assert.That(ObjectNameResolver.TryParse(selection, out var schema, out var name), Is.True);
            Assert.That(schema, Is.EqualTo("dbo"));
            Assert.That(name, Is.EqualTo("Users"));
        }

        [Test]
        [TestCase("SELECT * FROM dbo.Users")]
        [TestCase("dbo.Users AS u")]
        [TestCase("line1\nline2")]
        public void TryParse_Fails_WhenTheSelectionIsNotASingleIdentifier(string selection)
        {
            // 문장 전체를 선택한 경우 객체를 임의로 추측하지 않는다.
            Assert.That(ObjectNameResolver.TryParse(selection, out _, out _), Is.False);
        }

        [Test]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase(".")]
        [TestCase("[]")]
        public void TryParse_Fails_ForEmptyOrMeaninglessInput(string? selection)
        {
            Assert.That(ObjectNameResolver.TryParse(selection, out _, out _), Is.False);
        }

        [Test]
        public void TryParse_HandlesBracketedNamesContainingDots()
        {
            Assert.That(ObjectNameResolver.TryParse("[dbo].[My.Table]", out var schema, out var name), Is.True);
            Assert.That(schema, Is.EqualTo("dbo"));
            Assert.That(name, Is.EqualTo("My.Table"));
        }

        // ---------- 변경 목록에서 찾기 ----------

        private static ChangeRecord Record(string schema, string name)
            => new ChangeRecord
            {
                Schema = schema,
                ObjectName = name,
                QualifiedName = $"{schema}.{name}",
                RelativePath = $"{schema}/Tables/{name}.sql"
            };

        private static readonly List<ChangeRecord> Changes = new List<ChangeRecord>
        {
            Record("dbo", "Users"),
            Record("sales", "Orders"),
            Record("app", "Users")
        };

        [Test]
        public void FindMatch_MatchesOnSchemaQualifiedName()
        {
            var match = ObjectNameResolver.FindMatch(Changes, "sales", "Orders");

            Assert.That(match, Is.Not.Null);
            Assert.That(match!.QualifiedName, Is.EqualTo("sales.Orders"));
        }

        [Test]
        public void FindMatch_IsCaseInsensitive()
        {
            Assert.That(ObjectNameResolver.FindMatch(Changes, "SALES", "ORDERS"), Is.Not.Null);
        }

        [Test]
        public void FindMatch_PrefersDbo_WhenTheSchemaWasNotSpecified()
        {
            var match = ObjectNameResolver.FindMatch(Changes, null, "Users");

            Assert.That(match!.QualifiedName, Is.EqualTo("dbo.Users"),
                "스키마 없이 이름만 주어지면 dbo를 우선한다");
        }

        [Test]
        public void FindMatch_FallsBackToTheOnlyCandidate_WhenSchemaOmittedAndNoDboMatch()
        {
            var match = ObjectNameResolver.FindMatch(Changes, null, "Orders");

            Assert.That(match!.QualifiedName, Is.EqualTo("sales.Orders"));
        }

        [Test]
        public void FindMatch_ReturnsNull_WhenNothingMatches()
        {
            Assert.That(ObjectNameResolver.FindMatch(Changes, "dbo", "Nope"), Is.Null);
        }

        [Test]
        public void FindMatch_ReturnsNull_ForEmptyChangeList()
        {
            Assert.That(ObjectNameResolver.FindMatch(new List<ChangeRecord>(), "dbo", "Users"), Is.Null);
        }
    }
}
