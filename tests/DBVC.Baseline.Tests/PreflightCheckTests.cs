using DBVC.Baseline;

namespace DBVC.Baseline.Tests
{
    public class PreflightCheckTests
    {
        private static PreflightInput Clean() => new()
        {
            DirectoryExists = true,
            HasGitDirectory = true,
            HasSqlFiles = false
        };

        [Test]
        public void Validate_ReturnsNull_WhenEmptyGitClone()
        {
            Assert.That(PreflightCheck.Validate(Clean()), Is.Null);
        }

        [Test]
        public void Validate_ReturnsReason_WhenDirectoryMissing()
        {
            var input = Clean();
            input.DirectoryExists = false;

            Assert.That(PreflightCheck.Validate(input), Does.Contain("폴더"));
        }

        [Test]
        public void Validate_ReturnsReason_WhenNotGitRepository()
        {
            var input = Clean();
            input.HasGitDirectory = false;

            Assert.That(PreflightCheck.Validate(input), Does.Contain("git"));
        }

        [Test]
        public void Validate_ReturnsReason_WhenSqlFilesPresent()
        {
            var input = Clean();
            input.HasSqlFiles = true;

            Assert.That(PreflightCheck.Validate(input), Does.Contain(".sql"));
        }

        [Test]
        public void Validate_ReportsMissingDirectory_WhenNothingIsTrue()
        {
            // 폴더가 없으면 나머지 관측값은 의미가 없다. 사용자가 먼저 고칠 것을 말해야 한다.
            var input = new PreflightInput
            {
                DirectoryExists = false,
                HasGitDirectory = false,
                HasSqlFiles = true
            };

            Assert.That(PreflightCheck.Validate(input), Does.Contain("폴더"));
        }
    }
}
