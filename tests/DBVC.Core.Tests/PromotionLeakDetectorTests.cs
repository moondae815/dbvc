using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 공용 개발 DB에서 뜬 파일은 통짜 스냅샷이라 남의 변경이 섞인다. 운영 병합에서 그것을
    /// 알리는 유일한 자리다(스펙 3.4).
    /// </summary>
    [TestFixture]
    public class PromotionLeakDetectorTests
    {
        private const string Path = "dbo/StoredProcedures/usp_Order.sql";

        private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Lines(string path, params string[] lines) =>
            new Dictionary<string, IReadOnlyCollection<string>> { [path] = lines };

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> Branch(
            string branch, IReadOnlyDictionary<string, IReadOnlyCollection<string>> lines) =>
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> { [branch] = lines };

        [Test]
        public void Detect_ReportsLeak_WhenAddedLinesOverlapAnotherBranch()
        {
            var source = Lines(Path, "    DiscountRate DECIMAL(5,2)", "SELECT 1");
            var others = Branch("PROJ-120", Lines(Path, "DiscountRate DECIMAL(5,2)"));

            var leaks = PromotionLeakDetector.Detect(source, others);

            Assert.That(leaks, Has.Count.EqualTo(1));
            Assert.That(leaks[0].Path, Is.EqualTo(Path));
            Assert.That(leaks[0].BranchName, Is.EqualTo("PROJ-120"));
            Assert.That(leaks[0].Lines, Is.EqualTo(new[] { "DiscountRate DECIMAL(5,2)" }));
        }

        [Test]
        public void Detect_IgnoresTrivialLines_WhenOnlyKeywordsOverlap()
        {
            // 빼지 않으면 모든 프로시저가 모든 브랜치와 겹친다.
            var source = Lines(Path, "BEGIN", "end", "GO", "AS", "(", ")", ",", ";", "   ");
            var others = Branch("PROJ-120", Lines(Path, "BEGIN", "END", "go", "as", "(", ")", ",", ";", ""));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }

        [Test]
        public void Detect_ReturnsEmpty_WhenNoOverlap()
        {
            var source = Lines(Path, "DiscountRate DECIMAL(5,2)");
            var others = Branch("PROJ-120", Lines(Path, "Memo NVARCHAR(50)"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }

        [Test]
        public void Detect_NormalizesWhitespace_WhenComparingLines()
        {
            var source = Lines(Path, "\tWHERE  o.Id =\t@id\r\n");
            var others = Branch("PROJ-120", Lines(Path, "WHERE o.Id = @id"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Has.Count.EqualTo(1));
        }

        [Test]
        public void Detect_IsCaseSensitive_WhenLinesDifferOnlyInCase()
        {
            // 식별자·문자열 리터럴의 대소문자는 서로 다른 변경일 수 있다. 키워드만 무시 목록에서 가린다.
            var source = Lines(Path, "SELECT Name FROM dbo.Users");
            var others = Branch("PROJ-120", Lines(Path, "select name from dbo.users"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }

        [Test]
        public void Detect_IgnoresOtherPaths_WhenSameLineIsInADifferentFile()
        {
            var source = Lines(Path, "DiscountRate DECIMAL(5,2)");
            var others = Branch("PROJ-120", Lines("dbo/Tables/Orders.sql", "DiscountRate DECIMAL(5,2)"));

            Assert.That(PromotionLeakDetector.Detect(source, others), Is.Empty);
        }
    }
}
