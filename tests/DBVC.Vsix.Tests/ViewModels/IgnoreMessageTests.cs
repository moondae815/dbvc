using System;
using System.Collections.Generic;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 무시는 되돌리기보다 훨씬 센 동작이다 - 로그 행을 닫는 것은 공유 DB에서 전역이고,
    /// 닫힌 변경은 git에 영영 담기지 않는다. 그 사실이 확인 문구에서 빠지지 않게 한다.
    /// </summary>
    [TestFixture]
    public class IgnoreMessageTests
    {
        private static DiscardPlan PlanWith(string restore, string delete)
        {
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [restore] = "Modified",
                [delete] = "Added"
            };

            return DiscardPlan.Build(new[] { restore, delete }, states);
        }

        [Test]
        public void BuildIgnoreConfirmation_MentionsGlobalEffect_WhenRowsWillClose()
        {
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 2,
                foreignAuthors: new List<string>());

            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("함께 쓰는 모두에게"));
                Assert.That(text, Does.Contain("git"));
                // DDL을 취소하는 것으로 읽히면 안 된다.
                Assert.That(text, Does.Contain("데이터베이스의 변경은 그대로 남습니다"));
            });
        }

        [Test]
        public void BuildIgnoreConfirmation_OmitsGlobalEffect_WhenNothingWillClose()
        {
            // 닫을 행이 없으면 되돌리기와 같은 무게다. 없는 위험을 경고하면 문구가 값을 잃는다.
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 0,
                foreignAuthors: new List<string>());

            Assert.That(text, Does.Not.Contain("함께 쓰는 모두에게"));
        }

        [Test]
        public void BuildIgnoreConfirmation_CountsForeignItems_WhenOthersChangesSelected()
        {
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 2,
                foreignAuthors: new List<string> { "CORP\\jdoe" });

            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("다른 사람"));
                Assert.That(text, Does.Contain("CORP\\jdoe"));
            });
        }

        [Test]
        public void BuildIgnoreConfirmation_NamesFilesThatCannotBeRecovered()
        {
            // 미추적 파일은 지워지고 git이 갖고 있지 않다. 셈만으로는 부족하다.
            var text = ViewChangesViewModel.BuildIgnoreConfirmation(
                PlanWith("dbo/Tables/Foo.sql", "dbo/Views/Bar.sql"),
                rowsToClose: 1,
                foreignAuthors: new List<string>());

            Assert.That(text, Does.Contain("dbo/Views/Bar.sql"));
        }

        [Test]
        public void BuildIgnoreSummary_ReportsClosedRows_WhenRowsWereClosed()
        {
            var result = new DiscardResult();
            result.RestoredPaths.Add("dbo/Tables/Foo.sql");

            var text = ViewChangesViewModel.BuildIgnoreSummary(result, closedRows: 3);

            Assert.Multiple(() =>
            {
                Assert.That(text, Does.StartWith("무시했습니다"));
                Assert.That(text, Does.Contain("로그 3개 닫음"));
            });
        }

        [Test]
        public void BuildIgnoreSummary_DoesNotClaimSuccess_WhenNothingChanged()
        {
            var result = new DiscardResult();
            result.SkippedPaths.Add("dbo/Tables/Foo.sql");

            var text = ViewChangesViewModel.BuildIgnoreSummary(result, closedRows: 0);

            Assert.That(text, Does.Not.StartWith("무시했습니다"));
        }
    }
}
