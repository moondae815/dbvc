using System;
using System.IO;
using DBVC.Core;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class LegacyCredentialFileTests
    {
        private string _dir = null!;
        private string _file = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dbvc_legacy_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "credentials.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                try { Directory.Delete(_dir, true); } catch { }
            }
        }

        [Test]
        public void DeleteIfPresent_RemovesTheFile()
        {
            File.WriteAllText(_file, "[]");

            LegacyCredentialFile.DeleteIfPresent(_file);

            Assert.That(File.Exists(_file), Is.False);
        }

        [Test]
        public void DeleteIfPresent_KeepsTheDirectory()
        {
            // 같은 폴더에 mappings.json이 산다. 폴더를 지우면 매핑이 함께 사라진다.
            File.WriteAllText(_file, "[]");
            var mappings = Path.Combine(_dir, "mappings.json");
            File.WriteAllText(mappings, "[]");

            LegacyCredentialFile.DeleteIfPresent(_file);

            Assert.That(Directory.Exists(_dir), Is.True);
            Assert.That(File.Exists(mappings), Is.True);
        }

        [Test]
        public void DeleteIfPresent_IsQuiet_WhenTheFileIsNotThere()
        {
            Assert.DoesNotThrow(() => LegacyCredentialFile.DeleteIfPresent(_file));
        }

        [Test]
        public void DeleteIfPresent_SwallowsFailures()
        {
            // 디렉터리를 경로로 주면 File.Delete가 던진다. 삭제 실패로 플러그인이 뜨지
            // 않는 것과 옛 파일이 남는 것은 비교할 문제가 아니다.
            Assert.DoesNotThrow(() => LegacyCredentialFile.DeleteIfPresent(_dir));
            Assert.That(Directory.Exists(_dir), Is.True);
        }

        [Test]
        public void DefaultPath_PointsAtTheOldCredentialFile()
        {
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DBVC",
                "credentials.json");

            Assert.That(LegacyCredentialFile.DefaultPath, Is.EqualTo(expected));
        }
    }
}
