using DBVC.Baseline;
using DBVC.Core;
using DBVC.Core.Tests;

namespace DBVC.Baseline.Tests
{
    /// <summary>
    /// localhost의 SQL Server에 Windows 인증으로 붙어 임시 DB를 만든다.
    /// 접속되지 않으면 실패가 아니라 Skip이다 — CI에는 SQL Server가 없다.
    /// </summary>
    [TestFixture]
    public class BaselineRunnerIntegrationTests
    {
        [Test]
        public void Run_ReturnsClean_WhenExtractingRealDatabaseIntoEmptyFolder()
        {
            using var database = SqlServerTestDatabase.TryCreate(out var skipReason);
            if (database == null)
            {
                Assert.Ignore(skipReason ?? "SQL Server에 접속할 수 없습니다.");
                return;
            }

            database.ExecuteInOneSession(
                "CREATE TABLE dbo.Widget (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL)",
                "CREATE OR ALTER PROCEDURE dbo.usp_Widget AS SELECT 1");

            var repo = TempConfig.CreateDirectory();
            var configDirectory = TempConfig.CreateDirectory();
            try
            {
                var configManager = new ConfigManager(TempConfig.MappingFilePath(configDirectory));
                var smoManager = new SmoManager(configManager, credentialStore: null);

                var options = BaselineOptions.Parse(new[]
                {
                    "--server", SqlServerTestDatabase.ServerName,
                    "--database", database.Name,
                    "--repo", repo
                }).Options!;

                var report = new BaselineRunner(configManager, smoManager).Run(options);

                Assert.That(report.Verdict, Is.EqualTo(BaselineVerdict.Clean), report.Render());
                Assert.That(report.ExtractedCount, Is.GreaterThan(0));
                Assert.That(File.Exists(Path.Combine(repo, "dbo", "Tables", "Widget.sql")), Is.True);
            }
            finally
            {
                Directory.Delete(repo, recursive: true);
                Directory.Delete(configDirectory, recursive: true);
            }
        }
    }
}
