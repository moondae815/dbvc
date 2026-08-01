using System.Collections.Generic;
using System.Linq;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using NUnit.Framework;
using DBVC.Vsix.Services;

namespace DBVC.Vsix.Tests.Services
{
    [TestFixture]
    public class DiffTextBuilderTests
    {
        // DiffPlex의 Imaginary 줄은 Text가 null이다. 생성자 매개변수는 non-nullable로 선언되어 있어 `!`가 필요하다.
        private static DiffPiece Line(string? text, ChangeType type) => new DiffPiece(text!, type);

        [Test]
        public void Build_KeepsLineOrderAndJoinsWithNewlines()
        {
            var pane = DiffTextBuilder.Build(new[]
            {
                Line("CREATE TABLE Users (", ChangeType.Unchanged),
                Line("  Id INT", ChangeType.Unchanged),
                Line(");", ChangeType.Unchanged)
            });

            Assert.That(pane.Text, Is.EqualTo("CREATE TABLE Users (\n  Id INT\n);"));
        }

        [Test]
        public void Build_TurnsImaginaryLinesIntoEmptyPaddingLines()
        {
            var pane = DiffTextBuilder.Build(new[]
            {
                Line("A", ChangeType.Unchanged),
                Line(null, ChangeType.Imaginary),
                Line("B", ChangeType.Unchanged)
            });

            Assert.That(pane.Text, Is.EqualTo("A\n\nB"), "패딩 줄은 좌우 정렬을 위한 빈 줄입니다");
            Assert.That(pane.LineKinds[1], Is.EqualTo(DiffLineKind.Padding));
        }

        [TestCase(ChangeType.Unchanged, DiffLineKind.Unchanged)]
        [TestCase(ChangeType.Inserted, DiffLineKind.Inserted)]
        [TestCase(ChangeType.Deleted, DiffLineKind.Deleted)]
        [TestCase(ChangeType.Modified, DiffLineKind.Modified)]
        [TestCase(ChangeType.Imaginary, DiffLineKind.Padding)]
        public void Build_MapsEveryDiffPlexChangeType(ChangeType type, DiffLineKind expected)
        {
            var pane = DiffTextBuilder.Build(new[] { Line("x", type) });

            Assert.That(pane.LineKinds.Single(), Is.EqualTo(expected));
        }

        [Test]
        public void Build_ProducesOneLineKindPerTextLine()
        {
            var model = SideBySideDiffBuilder.Diff("A\nB\nC", "A\nX\nC\nD");

            var oldPane = DiffTextBuilder.Build(model.OldText.Lines);
            var newPane = DiffTextBuilder.Build(model.NewText.Lines);

            Assert.That(oldPane.LineKinds.Count, Is.EqualTo(oldPane.Text.Split('\n').Length),
                "렌더러가 줄 번호로 종류를 찾으므로 개수가 어긋나면 안 됩니다");
            Assert.That(newPane.LineKinds.Count, Is.EqualTo(newPane.Text.Split('\n').Length));
            Assert.That(oldPane.LineKinds.Count, Is.EqualTo(newPane.LineKinds.Count),
                "좌우 줄 수가 같아야 스크롤 동기화가 의미를 가집니다");
        }

        [Test]
        public void Build_ReturnsOneUnchangedLineKind_ForNullOrEmptyInput()
        {
            // 빈 문서도 실제로는 1줄(내용 없는 한 줄)이다. Text.Split('\n')도 그렇게 센다.
            // LineKinds가 비어 있으면 줄 번호로 색을 찾는 렌더러와 개수가 어긋난다.
            var fromNull = DiffTextBuilder.Build(null);
            Assert.That(fromNull.Text, Is.Empty);
            Assert.That(fromNull.LineKinds, Is.EqualTo(new[] { DiffLineKind.Unchanged }));
            Assert.That(fromNull.LineKinds.Count, Is.EqualTo(fromNull.Text.Split('\n').Length));

            var fromEmptyList = DiffTextBuilder.Build(new List<DiffPiece>());
            Assert.That(fromEmptyList.Text, Is.Empty);
            Assert.That(fromEmptyList.LineKinds, Is.EqualTo(new[] { DiffLineKind.Unchanged }));
            Assert.That(fromEmptyList.LineKinds.Count, Is.EqualTo(fromEmptyList.Text.Split('\n').Length));
        }
    }
}
