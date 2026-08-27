using NUnit.Framework;
using DBVC.Core.Models;
using DBVC.Vsix.ViewModels;

namespace DBVC.Vsix.Tests.ViewModels
{
    /// <summary>
    /// 운영에는 트리거를 설치할 수 없으므로 차이가 "미배포"인지 "무단 변경"인지 구분할 수 없다.
    /// 구분되는 척하면 DBA가 잘못된 판단을 한다.
    /// </summary>
    [TestFixture]
    public class DifferenceItemViewModelTests
    {
        [Test]
        public void GetStateText_DistinguishesEachState_WhenModeIsDeploy()
        {
            Assert.That(DifferenceTextProvider.GetStateText(ObjectDiffState.MissingInDatabase, MappingMode.Deploy),
                Is.EqualTo("배포 필요 (신규)"));
            Assert.That(DifferenceTextProvider.GetStateText(ObjectDiffState.Modified, MappingMode.Deploy),
                Is.EqualTo("배포 필요 (내용 다름)"));
            Assert.That(DifferenceTextProvider.GetStateText(ObjectDiffState.MissingInBranch, MappingMode.Deploy),
                Is.EqualTo("DB에만 있음"));
        }

        [TestCase(ObjectDiffState.MissingInDatabase)]
        [TestCase(ObjectDiffState.Modified)]
        [TestCase(ObjectDiffState.MissingInBranch)]
        public void GetStateText_ReportsNeedsReview_ForEveryState_WhenModeIsAudit(ObjectDiffState state)
        {
            Assert.That(DifferenceTextProvider.GetStateText(state, MappingMode.Audit), Is.EqualTo("확인 필요"));
        }

        [Test]
        public void Constructor_TranslatesObjectTypeIntoKorean()
        {
            var difference = new SchemaDifference("dbo.GetUser", "dbo/StoredProcedures/GetUser.sql", "StoredProcedure", ObjectDiffState.Modified);

            var item = new DifferenceItemViewModel(difference, MappingMode.Deploy);

            Assert.That(item.QualifiedName, Is.EqualTo("dbo.GetUser"));
            Assert.That(item.ObjectTypeText, Is.EqualTo("저장 프로시저"));
            Assert.That(item.StateText, Is.EqualTo("배포 필요 (내용 다름)"));
        }

        [Test]
        public void Constructor_FallsBackToTheRawType_WhenItIsNotKnown()
        {
            var difference = new SchemaDifference("dbo.X", "dbo/Other/X.sql", "Other", ObjectDiffState.Modified);

            var item = new DifferenceItemViewModel(difference, MappingMode.Deploy);

            Assert.That(item.ObjectTypeText, Is.EqualTo("Other"));
        }
    }
}
