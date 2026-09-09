using DBVC.Baseline;
using DBVC.Core;
using DBVC.Core.Models;
using Moq;

namespace DBVC.Baseline.Tests
{
    public class BaselineRunnerTests
    {
        private const string Server = "PRODSRV";
        private const string Database = "SalesDB";
        private const string Repo = @"D:\dbvc\prod";

        private static BaselineOptions Options() =>
            BaselineOptions.Parse(new[] { "--server", Server, "--database", Database, "--repo", Repo }).Options!;

        private static ScriptResult CleanScript(int count)
        {
            return new ScriptResult { SucceededCount = count };
        }

        private static ComparisonResult CleanComparison(int compared)
        {
            return new ComparisonResult { ComparedCount = compared, RepositoryScanCompleted = true };
        }

        [Test]
        public void Run_WritesWriteModeMapping_BeforeExtraction()
        {
            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();

            // 마지막으로 쓰인 mode를 계속 덮어 담는다. 추출 시점에는 Write여야 하고,
            // Run이 끝난 뒤에는 검증 직전에 덮어쓴 Audit이 남아 있어야 한다.
            MappingMode? lastWrittenMode = null;

            config.Setup(c => c.AddMapping(It.IsAny<MappingConfig>()))
                  .Callback<MappingConfig>(m => lastWrittenMode = m.Mode);
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(() =>
               {
                   Assert.That(lastWrittenMode, Is.EqualTo(MappingMode.Write), "추출 시점에는 Write여야 한다");
                   return CleanScript(10);
               });
            smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(() =>
               {
                   // 끝난 뒤의 상태만 보면 Compare → SetMode(Audit) 순서로 뒤집혀도 같은 결과가
                   // 나와 통과해 버린다. 호출되는 그 순간에 이미 Audit이어야 한다고 못 박는다.
                   Assert.That(lastWrittenMode, Is.EqualTo(MappingMode.Audit), "비교 시점에는 Audit이어야 한다");
                   return CleanComparison(10);
               });

            new BaselineRunner(config.Object, smo.Object).Run(Options());

            Assert.That(lastWrittenMode, Is.EqualTo(MappingMode.Audit), "검증 직전에 Audit으로 덮어써야 한다");
        }

        [Test]
        public void Run_WritesOptionValuesIntoMapping_WhenRunning()
        {
            // Mode만 보면 ServerName·DatabaseName·GitPath가 엉뚱한 값이어도 잡아내지 못한다.
            // GitPath가 틀리면 운영 스냅샷이 엉뚱한 폴더에 쓰이는데 조용히 통과해 버린다.
            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();
            var writtenMappings = new List<MappingConfig>();

            config.Setup(c => c.AddMapping(It.IsAny<MappingConfig>()))
                  .Callback<MappingConfig>(m => writtenMappings.Add(m));
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(CleanScript(10));
            smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(CleanComparison(10));

            new BaselineRunner(config.Object, smo.Object).Run(Options());

            Assert.That(writtenMappings.Count, Is.EqualTo(2));
            foreach (var mapping in writtenMappings)
            {
                Assert.That(mapping.ServerName, Is.EqualTo(Server));
                Assert.That(mapping.DatabaseName, Is.EqualTo(Database));
                Assert.That(mapping.GitPath, Is.EqualTo(Repo));
            }
        }

        [Test]
        public void Run_PassesNullObjectNames_ToExtraction()
        {
            // 목록을 넘기면 조용히 부분 기준선이 만들어진다.
            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(CleanScript(3));
            smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(CleanComparison(3));

            new BaselineRunner(config.Object, smo.Object).Run(Options());

            smo.Verify(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Run_ReturnsClean_WhenBothStepsClean()
        {
            var report = RunWith(CleanScript(42), CleanComparison(42));

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.Clean));
            Assert.That(report.ExtractedCount, Is.EqualTo(42));
            Assert.That(report.ComparedCount, Is.EqualTo(42));
        }

        [Test]
        public void Run_ReturnsExtractionFailed_WhenScriptResultHasFailures()
        {
            var script = CleanScript(5);
            script.FailedObjects.Add("dbo.usp_Secret");

            var report = RunWith(script, CleanComparison(5));

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.ExtractionFailed));
            Assert.That(report.ExtractionFailures, Does.Contain("dbo.usp_Secret"));
        }

        [Test]
        public void Run_SkipsComparison_WhenExtractionFailed()
        {
            // 이미 불완전한 기준선을 검증해도 새로 알 것이 없다. 운영을 두 번 읽지 않는다.
            var script = CleanScript(5);
            script.FailedObjects.Add("dbo.usp_Secret");

            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(script);

            new BaselineRunner(config.Object, smo.Object).Run(Options());

            smo.Verify(s => s.CompareWithRepository(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void Run_ReturnsExtractionFailed_WhenScriptResultIsNull()
        {
            // 매핑이 없거나 접속에 실패하면 null이다.
            var report = RunWith(null, CleanComparison(0));

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.ExtractionFailed));
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenComparisonHasDifferences()
        {
            var comparison = CleanComparison(10);
            comparison.Differences.Add(new SchemaDifference("dbo.P", "dbo/StoredProcedures/P.sql", "StoredProcedure", ObjectDiffState.Modified));

            var report = RunWith(CleanScript(10), comparison);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
            Assert.That(report.VerificationFindings.Count, Is.EqualTo(1));
            Assert.That(report.VerificationFindings[0], Does.Contain("dbo.P"));
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenComparisonHasUnverifiedObjects()
        {
            // IsInSync는 "판정하지 못한 객체"를 담지 않는다. 차이가 0이어도 일치가 아니다.
            var comparison = CleanComparison(10);
            comparison.FailedObjects.Add("dbo.Q");

            var report = RunWith(CleanScript(10), comparison);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenRepositoryScanIncomplete()
        {
            var comparison = CleanComparison(10);
            comparison.RepositoryScanCompleted = false;

            var report = RunWith(CleanScript(10), comparison);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
            Assert.That(report.RepositoryScanCompleted, Is.False);
        }

        [Test]
        public void Run_ReturnsVerificationFailed_WhenComparisonIsNull()
        {
            var report = RunWith(CleanScript(10), null);

            Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.VerificationFailed));
        }

        private static BaselineReport RunWith(ScriptResult? script, ComparisonResult? comparison)
        {
            var config = new Mock<IConfigManager>();
            var smo = new Mock<ISmoManager>();
            smo.Setup(s => s.ScriptObjectsDetailed(Server, Database, null, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(script);
            smo.Setup(s => s.CompareWithRepository(Server, Database, It.IsAny<IProgress<ExtractionProgress>?>(), It.IsAny<CancellationToken>()))
               .Returns(comparison);

            return new BaselineRunner(config.Object, smo.Object).Run(Options());
        }
    }
}
