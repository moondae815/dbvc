using System;
using System.Collections.Generic;
using System.IO;
using DBVC.Core;
using LibGit2Sharp;
using NUnit.Framework;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class GitIdentityTests
    {
        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    // .git 내부에는 읽기 전용 파일이 있을 수 있다.
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        private string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "dbvc_ident_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            _tempDirs.Add(path);
            return path;
        }

        /// <summary>
        /// 신원이 비어 있는 저장소를 만든다. 전역 config가 있는 기계에서도 판정이 흔들리지
        /// 않도록 로컬에 빈 값을 심는다 - BuildSignature의 탐색은 로컬 → 전역 → 시스템이라,
        /// 로컬을 비워 두기만 하면 실행 기계의 전역 설정이 결과를 바꾼다.
        /// </summary>
        private string NewRepoWithoutIdentity()
        {
            var path = NewTempDir();
            Repository.Init(path);
            using (var repo = new Repository(path))
            {
                repo.Config.Set("user.name", string.Empty, ConfigurationLevel.Local);
                repo.Config.Set("user.email", string.Empty, ConfigurationLevel.Local);
            }
            return path;
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenThePathIsNotARepository()
        {
            // 판정할 수 없는 것을 "없음"으로 뭉개면 배너가 상시로 뜬다.
            Assert.That(GitIdentity.Detect(NewTempDir()), Is.EqualTo(GitIdentityState.Unknown));
        }

        [Test]
        public void Detect_ReturnsUnknown_WhenThePathIsEmpty()
        {
            Assert.That(GitIdentity.Detect("  "), Is.EqualTo(GitIdentityState.Unknown));
        }

        [Test]
        public void Detect_ReturnsMissing_WhenTheRepositoryHasNoIdentity()
        {
            Assert.That(GitIdentity.Detect(NewRepoWithoutIdentity()), Is.EqualTo(GitIdentityState.Missing));
        }

        [Test]
        public void Detect_ReturnsConfigured_AfterWrite()
        {
            var path = NewRepoWithoutIdentity();

            GitIdentity.Write(path, "홍길동", "gildong@example.com");

            Assert.That(GitIdentity.Detect(path), Is.EqualTo(GitIdentityState.Configured));
        }

        [Test]
        public void Write_TouchesOnlyTheLocalConfig()
        {
            // SSMS 확장이 개발자의 전역 git 설정을 조용히 바꾸면 다른 프로젝트의 커밋
            // 작성자까지 바뀐다. 되돌리는 길은 도구 안에 없다.
            //
            // 네 조합(이름/메일 × 로컬/전역)을 전부 본다 - 이름만 로컬에 쓰고 메일은 전역에
            // 새는 것과 그 반대를 모두 잡아야 하며, 값도 실행마다 고유해야 한다. 개발자의
            // 실제 전역 신원이 우연히 리터럴과 같으면 Not.EqualTo가 거짓으로 실패한다.
            var path = NewRepoWithoutIdentity();
            var suffix = Guid.NewGuid().ToString("N");
            var name = "홍길동-" + suffix;
            var email = $"gildong-{suffix}@example.com";

            GitIdentity.Write(path, name, email);

            using var repo = new Repository(path);
            Assert.Multiple(() =>
            {
                Assert.That(repo.Config.Get<string>("user.name", ConfigurationLevel.Local)?.Value,
                    Is.EqualTo(name));
                Assert.That(repo.Config.Get<string>("user.email", ConfigurationLevel.Local)?.Value,
                    Is.EqualTo(email));
                Assert.That(repo.Config.Get<string>("user.name", ConfigurationLevel.Global)?.Value,
                    Is.Not.EqualTo(name),
                    "전역 config에 쓰면 안 된다");
                Assert.That(repo.Config.Get<string>("user.email", ConfigurationLevel.Global)?.Value,
                    Is.Not.EqualTo(email),
                    "전역 config에 쓰면 안 된다");
            });
        }

        [Test]
        public void Write_TrimsSurroundingWhitespace()
        {
            var path = NewRepoWithoutIdentity();

            GitIdentity.Write(path, "  홍길동  ", "  gildong@example.com  ");

            using var repo = new Repository(path);
            Assert.Multiple(() =>
            {
                Assert.That(repo.Config.Get<string>("user.name")?.Value, Is.EqualTo("홍길동"));
                Assert.That(repo.Config.Get<string>("user.email")?.Value, Is.EqualTo("gildong@example.com"));
            });
        }

        [TestCase(null, "gildong@example.com", TestName = "Validate_Rejects_WhenNameIsNull")]
        [TestCase("   ", "gildong@example.com", TestName = "Validate_Rejects_WhenNameIsBlank")]
        [TestCase("홍길동", null, TestName = "Validate_Rejects_WhenEmailIsNull")]
        [TestCase("홍길동", "   ", TestName = "Validate_Rejects_WhenEmailIsBlank")]
        [TestCase("홍길동", "gildong", TestName = "Validate_Rejects_WhenEmailHasNoAtSign")]
        [TestCase("홍길동", "gildong@example", TestName = "Validate_Rejects_WhenEmailHasNoDot")]
        [TestCase("홍길동", "gil dong@example.com", TestName = "Validate_Rejects_WhenEmailHasWhitespace")]
        [TestCase("홍길동", "@example.com", TestName = "Validate_Rejects_WhenEmailHasNoLocalPart")]
        [TestCase("홍길\r\n동", "gildong@example.com", TestName = "Validate_Rejects_WhenNameHasNewline")]
        [TestCase("홍길동", "gil\r\ndong@example.com", TestName = "Validate_Rejects_WhenEmailHasNewline")]
        public void Validate_ReturnsAKoreanReason_WhenInputIsUnusable(string? name, string? email)
        {
            var reason = GitIdentity.Validate(name, email);

            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("홍길동", "gildong@example.com")]
        [TestCase("Gil-Dong Hong", "gil.dong@corp.example.co.kr")]
        public void Validate_ReturnsNull_WhenInputIsUsable(string name, string email)
        {
            Assert.That(GitIdentity.Validate(name, email), Is.Null);
        }
    }
}
