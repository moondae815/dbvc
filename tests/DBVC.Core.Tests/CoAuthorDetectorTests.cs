using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 공용 DB가 하나뿐인 이상 같은 객체를 둘이 만지는 것을 막을 수 없다.
    /// 막을 수는 없고 알릴 수는 있다 - 커밋하는 내용에 남의 미커밋 작업이 들어 있다는 사실을.
    /// </summary>
    [TestFixture]
    public class CoAuthorDetectorTests
    {
        private static ChangeLogRow Row(string schema, string name, string login, string host)
            => new ChangeLogRow { SchemaName = schema, ObjectName = name, LoginName = login, HostName = host };

        [Test]
        public void Detect_ReturnsWarning_WhenAnotherHostTouchedTheSameObject()
        {
            var rows = new[]
            {
                Row("dbo", "P", "app_dev", "MY-PC"),
                Row("dbo", "P", "app_dev", "KIM-PC")
            };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0].QualifiedName, Is.EqualTo("dbo.P"));
            Assert.That(warnings[0].Author, Is.EqualTo("KIM-PC"));
        }

        [Test]
        public void Detect_ReturnsNothing_WhenOnlyCurrentAuthorTouchedIt()
        {
            var rows = new[] { Row("dbo", "P", "app_dev", "MY-PC") };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void Detect_IgnoresObjectsNotBeingCommitted()
        {
            // 커밋하지 않는 객체를 남이 만졌다는 사실은 지금 알릴 일이 아니다.
            var rows = new[] { Row("dbo", "Q", "app_dev", "KIM-PC") };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void Detect_ReportsEachOtherAuthorOnce_WhenTheyTouchedItRepeatedly()
        {
            var rows = new[]
            {
                Row("dbo", "P", "app_dev", "KIM-PC"),
                Row("dbo", "P", "app_dev", "KIM-PC"),
                Row("dbo", "P", "app_dev", "LEE-PC")
            };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings.Select(w => w.Author), Is.EquivalentTo(new[] { "KIM-PC", "LEE-PC" }));
        }

        [Test]
        public void Detect_MatchesQualifiedNameIgnoringCase()
        {
            var rows = new[] { Row("dbo", "P", "app_dev", "KIM-PC") };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "DBO.p" }, "app_dev", "MY-PC");

            Assert.That(warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void Detect_TreatsNullHostAsAnotherAuthor()
        {
            // v3 이전 행은 작업자를 알 수 없다. "내 것"으로 볼 근거가 없으므로 남의 것으로 다룬다.
            var rows = new[] { Row("dbo", "P", "app_dev", null!) };

            var warnings = CoAuthorDetector.Detect(rows, new[] { "dbo.P" }, "app_dev", "MY-PC");

            Assert.That(warnings, Has.Count.EqualTo(1));
        }
    }
}
