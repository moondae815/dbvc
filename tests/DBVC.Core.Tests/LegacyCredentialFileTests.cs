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
            // FileShare.None으로 여는 방식은 강제 잠금이라, Linux/macOS에서는 권고 잠금이라
            // File.Delete가 그냥 성공해 이 테스트가 검증하려는 실패 자체가 일어나지 않는다.
            // File.SetAttributes로 읽기 전용을 걸면 두 플랫폼 모두에서 File.Delete가
            // UnauthorizedAccessException을 던지므로 이식성 있게 실패를 강제할 수 있다.
            // 삭제 실패로 플러그인이 뜨지 않는 것과 옛 파일이 남는 것은 비교할 문제가 아니다.
            File.WriteAllText(_file, "[]");
            File.SetAttributes(_file, FileAttributes.ReadOnly);

            try
            {
                Assert.DoesNotThrow(() => LegacyCredentialFile.DeleteIfPresent(_file));

                Assert.That(File.Exists(_file), Is.True);
            }
            finally
            {
                // 읽기 전용을 풀어야 TearDown의 Directory.Delete가 정리를 끝낼 수 있다.
                File.SetAttributes(_file, FileAttributes.Normal);
            }
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
