using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class BaselineOptionsTests
    {
        private static string[] Valid() => new[]
        {
            "--server", "PRODSRV", "--database", "SalesDB", "--repo", @"D:\dbvc\prod"
        };

        [Test]
        public void Parse_ReturnsOptions_WhenRequiredArgsGiven()
        {
            var result = BaselineOptions.Parse(Valid());

            Assert.That(result.IsValid, Is.True, result.Error);
            Assert.That(result.Options!.Server, Is.EqualTo("PRODSRV"));
            Assert.That(result.Options.Database, Is.EqualTo("SalesDB"));
            Assert.That(result.Options.RepositoryPath, Is.EqualTo(@"D:\dbvc\prod"));
        }

        [Test]
        public void Parse_LeavesSqlUserNull_WhenFlagOmitted()
        {
            var result = BaselineOptions.Parse(Valid());

            Assert.That(result.Options!.SqlUser, Is.Null);
        }

        [Test]
        public void Parse_ReadsSqlUser_WhenFlagGiven()
        {
            var args = new List<string>(Valid()) { "--sql-user", "dbvc_reader" };

            var result = BaselineOptions.Parse(args);

            Assert.That(result.Options!.SqlUser, Is.EqualTo("dbvc_reader"));
        }

        [Test]
        public void Parse_ReturnsError_WhenServerMissing()
        {
            var result = BaselineOptions.Parse(new[] { "--database", "SalesDB", "--repo", "C:\\x" });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("--server"));
        }

        [Test]
        public void Parse_ReturnsError_WhenPasswordFlagGiven()
        {
            var args = new List<string>(Valid()) { "--password", "hunter2" };

            var result = BaselineOptions.Parse(args);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("암호"));
        }

        [Test]
        public void Parse_ReturnsError_WhenUnknownFlagGiven()
        {
            // 값을 함께 준다. 값이 없으면 "값이 없습니다" 갈래로 빠져 엉뚱한 이유로 통과한다.
            var args = new List<string>(Valid()) { "--force", "true" };

            var result = BaselineOptions.Parse(args);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("모르는 옵션"));
        }

        [Test]
        public void Parse_ReturnsError_WhenFlagHasNoValue()
        {
            var result = BaselineOptions.Parse(new[] { "--server" });

            Assert.That(result.IsValid, Is.False);
        }
    }
}
