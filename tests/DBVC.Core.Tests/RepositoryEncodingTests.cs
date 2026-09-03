using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class RepositoryEncodingTests
    {
        private static string NewRepoDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_repoenc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>규약(<c>[Schema]/[Type]/[Name].sql</c>)에 맞는 자리에 파일을 놓는다.</summary>
        private static void WriteObject(string repoPath, string encoding)
        {
            var dir = Path.Combine(repoPath, "dbo", "Tables");
            Directory.CreateDirectory(dir);

            var enc = encoding == "utf16"
                ? (Encoding)new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

            File.WriteAllText(Path.Combine(dir, "Users.sql"), "CREATE TABLE dbo.Users (Id int);", enc);
        }

        [Test]
        public void Detect_ReturnsLegacy_WhenTheFilesAreUtf16()
        {
            var repo = NewRepoDir();
            WriteObject(repo, "utf16");

            Assert.That(RepositoryEncoding.Detect(repo), Is.EqualTo(RepositoryEncodingKind.Legacy));
        }

        [Test]
        public void Detect_ReturnsCurrent_WhenTheFilesAreUtf8()
        {
            var repo = NewRepoDir();
            WriteObject(repo, "utf8");

            Assert.That(RepositoryEncoding.Detect(repo), Is.EqualTo(RepositoryEncodingKind.Current));
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenNoExtractedObjectExists()
        {
            // 갓 연결한 저장소다. 판정할 근거가 없으므로 배너를 띄우면 안 된다 -
            // 전 파일 재작성을 권하는 안내가 빈 저장소에 뜨는 것은 명백히 틀렸다.
            Assert.That(RepositoryEncoding.Detect(NewRepoDir()), Is.EqualTo(RepositoryEncodingKind.Unknown));
        }

        [Test]
        public void Detect_IgnoresSqlFilesOutsideTheConvention()
        {
            // 사용자가 루트에 넣어 둔 .sql을 판정 근거로 삼으면, 그 파일 하나 때문에 멀쩡한
            // 저장소에 전환 배너가 뜬다. ExtractionBaseline과 같은 엄격함이다.
            var repo = NewRepoDir();
            File.WriteAllText(Path.Combine(repo, "adhoc.sql"), "SELECT 1;",
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            Assert.That(RepositoryEncoding.Detect(repo), Is.EqualTo(RepositoryEncodingKind.Unknown));
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenThePathDoesNotExist()
        {
            Assert.That(
                RepositoryEncoding.Detect(Path.Combine(Path.GetTempPath(), "dbvc_missing_" + Guid.NewGuid().ToString("N"))),
                Is.EqualTo(RepositoryEncodingKind.Unknown));
        }

        [Test]
        public void GitAttributesContent_TurnsOffEolConversionForSqlFiles()
        {
            // text eol=crlf를 쓰면 블롭은 LF, 작업 트리는 CRLF가 된다. Diff의 Old는 블롭에서
            // New는 작업 트리에서 오므로 모든 줄이 변경으로 보인다. 실측으로 확인한 함정이다.
            Assert.Multiple(() =>
            {
                Assert.That(RepositoryEncoding.GitAttributesContent, Does.Contain("*.sql -text"));
                Assert.That(RepositoryEncoding.GitAttributesContent, Does.Not.Contain("eol=crlf"));
            });
        }

        [Test]
        public void EnsureGitAttributes_WritesTheFile_WhenItIsMissing()
        {
            var repo = NewRepoDir();

            Assert.That(RepositoryEncoding.EnsureGitAttributes(repo), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(repo, ".gitattributes")), Does.Contain("*.sql -text"));
        }

        [Test]
        public void EnsureGitAttributes_LeavesAnExistingFileAlone()
        {
            // 사용자가 손으로 넣은 규칙을 덮어쓰면 그 사람의 저장소 설정이 조용히 사라진다.
            var repo = NewRepoDir();
            var path = Path.Combine(repo, ".gitattributes");
            File.WriteAllText(path, "* text=auto\n");

            Assert.That(RepositoryEncoding.EnsureGitAttributes(repo), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("* text=auto\n"));
        }

        [Test]
        public void EnsureGitAttributes_WritesWithoutABom()
        {
            // .gitattributes는 Git이 읽는 설정 파일이다. BOM을 붙이면 첫 줄을 규칙으로
            // 알아보지 못할 위험이 있다 - 저장소 .sql에 BOM을 붙이는 것과 목적이 다르다.
            var repo = NewRepoDir();
            RepositoryEncoding.EnsureGitAttributes(repo);

            var bytes = File.ReadAllBytes(Path.Combine(repo, ".gitattributes"));
            Assert.That(new[] { bytes[0], bytes[1], bytes[2] }, Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        }
    }
}
