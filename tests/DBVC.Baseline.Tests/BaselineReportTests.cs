using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class BaselineReportTests
    {
        [Test]
        public void ExitCode_ReturnsZero_WhenClean()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.Clean };

            Assert.That(report.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void ExitCode_ReturnsOne_WhenExtractionFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.ExtractionFailed };

            Assert.That(report.ExitCode, Is.EqualTo(1));
        }

        [Test]
        public void ExitCode_ReturnsTwo_WhenVerificationFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.VerificationFailed };

            Assert.That(report.ExitCode, Is.EqualTo(2));
        }

        [Test]
        public void ExitCode_ReturnsThree_WhenPreflightFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.PreflightFailed };

            Assert.That(report.ExitCode, Is.EqualTo(3));
        }

        [Test]
        public void Render_ListsEveryExtractionFailure()
        {
            // 조용히 빠지는 것이 이 도구의 유일한 치명적 실패 방식이다. 전부 출력해야 한다.
            var report = new BaselineReport { Verdict = BaselineVerdict.ExtractionFailed };
            report.ExtractionFailures.Add("dbo.usp_A");
            report.ExtractionFailures.Add("dbo.usp_B");

            var text = report.Render();

            Assert.That(text, Does.Contain("dbo.usp_A"));
            Assert.That(text, Does.Contain("dbo.usp_B"));
        }

        [Test]
        public void Render_SaysDoNotCommit_WhenVerificationFailed()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.VerificationFailed };
            report.VerificationFindings.Add("dbo.P — 수정됨");

            var text = report.Render();

            Assert.That(text, Does.Contain("커밋하지 마세요"));
            Assert.That(text, Does.Contain("dbo.P"));
        }

        [Test]
        public void Render_SaysSafeToCommit_WhenClean()
        {
            var report = new BaselineReport { Verdict = BaselineVerdict.Clean, ExtractedCount = 1234 };

            var text = report.Render();

            Assert.That(text, Does.Contain("1234"));
            Assert.That(text, Does.Contain("커밋해도 됩니다"));
        }
    }
}
