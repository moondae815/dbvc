using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    /// <summary>
    /// 되돌리기의 판정은 여기 하나뿐이다. 화면과 GitManager가 각자 판정하면 갈라지고,
    /// 갈라지는 날 "지울 파일 0개"라고 말해 놓고 지운다.
    /// </summary>
    [TestFixture]
    public class DiscardPlanTests
    {
        private static Dictionary<string, string> States(params (string Path, string State)[] entries)
        {
            var states = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries) states[entry.Path] = entry.State;
            return states;
        }

        [TestCase("Modified")]
        [TestCase("Deleted")]
        public void Build_PutsPathInRestore_WhenGitReportsTrackedChange(string state)
        {
            var plan = DiscardPlan.Build(
                new[] { "dbo/Tables/Users.sql" },
                States(("dbo/Tables/Users.sql", state)));

            Assert.That(plan.RestorePaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
            Assert.That(plan.DeletePaths, Is.Empty);
        }

        [Test]
        public void Build_PutsPathInDelete_WhenGitReportsAdded()
        {
            var plan = DiscardPlan.Build(
                new[] { "dbo/Views/vSales.sql" },
                States(("dbo/Views/vSales.sql", "Added")));

            Assert.That(plan.DeletePaths, Is.EqualTo(new[] { "dbo/Views/vSales.sql" }));
            Assert.That(plan.RestorePaths, Is.Empty);
        }

        /// <summary>
        /// 화면 목록은 사용자가 들여다본 만큼 낡는다. 그 사이에 깨끗해진 파일을 HEAD로
        /// 덮어쓰는 것은 되돌리기가 아니라 손실이다.
        /// </summary>
        [Test]
        public void Build_SkipsPath_WhenGitNoLongerReportsIt()
        {
            var plan = DiscardPlan.Build(
                new[] { "dbo/Tables/Users.sql" },
                States(("dbo/Tables/Orders.sql", "Modified")));

            Assert.That(plan.RestorePaths, Is.Empty);
            Assert.That(plan.DeletePaths, Is.Empty);
            Assert.That(plan.SkippedPaths, Is.EqualTo(new[] { "dbo/Tables/Users.sql" }));
        }

        /// <summary>DBVC가 만든 파일이 아니면 되돌리기의 대상이 아니다.</summary>
        [TestCase("README.md")]
        [TestCase(".gitattributes")]
        [TestCase("dbo/Tables/Users.txt")]
        public void Build_SkipsPath_WhenItIsNotADbvcObjectFile(string path)
        {
            var plan = DiscardPlan.Build(new[] { path }, States((path, "Modified")));

            Assert.That(plan.SkippedPaths, Is.EqualTo(new[] { path }));
        }

        /// <summary>
        /// ".." 세 조각은 경로 규약 검사를 그대로 통과한다(WorkingTreeCleaner에 같은 함정이
        /// 기록되어 있다). 여기서 걸러 두어야 저장소 밖 파일이 대상이 되지 않는다.
        /// </summary>
        [Test]
        public void Build_SkipsPath_WhenItEscapesTheRepositoryRoot()
        {
            var plan = DiscardPlan.Build(
                new[] { "../../evil.sql" },
                States(("../../evil.sql", "Modified")));

            Assert.That(plan.SkippedPaths, Is.EqualTo(new[] { "../../evil.sql" }));
            Assert.That(plan.RestorePaths, Is.Empty);
        }

        [Test]
        public void Build_ReturnsEmptyPlan_WhenNothingIsEligible()
        {
            var plan = DiscardPlan.Build(new[] { "README.md" }, States(("README.md", "Modified")));

            Assert.That(plan.IsEmpty, Is.True);
        }

        [Test]
        public void Build_ReturnsEmptyPlan_WhenInputsAreNull()
        {
            var plan = DiscardPlan.Build(null, null);

            Assert.That(plan.IsEmpty, Is.True);
            Assert.That(plan.SkippedPaths, Is.Empty);
        }
    }
}
