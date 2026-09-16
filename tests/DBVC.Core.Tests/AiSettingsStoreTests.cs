using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class AiSettingsStoreTests
    {
        private string _path = string.Empty;

        /// <summary>
        /// 보호를 흉내만 낸다. 진짜 DPAPI를 타면 이 픽스처가 검증하는 것이
        /// 저장 로직인지 암호화인지 구분되지 않는다.
        /// </summary>
        private sealed class ReversibleProtector : ISecretProtector
        {
            public string Protect(string plainText) =>
                "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

            public string? Unprotect(string protectedText) =>
                protectedText.StartsWith("enc:", StringComparison.Ordinal)
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText.Substring(4)))
                    : null;
        }

        [SetUp]
        public void CreateTempPath()
        {
            _path = Path.Combine(Path.GetTempPath(), "dbvc_ai_" + Guid.NewGuid().ToString("N"), "ai-settings.json");
        }

        [TearDown]
        public void DeleteTempPath()
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void Load_ReturnsSavedValues_WhenRoundTripped()
        {
            var store = new AiSettingsStore(_path, new ReversibleProtector());
            store.Save(new AiSettings
            {
                BaseUrl = "https://llm.example.com/v1",
                Model = "gpt-4o-mini",
                ApiKey = "sk-secret-value",
                TimeoutSeconds = 45,
                MaxDiffLines = 200,
                ConsentedHost = "llm.example.com",
            });

            var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

            Assert.Multiple(() =>
            {
                Assert.That(loaded.BaseUrl, Is.EqualTo("https://llm.example.com/v1"));
                Assert.That(loaded.Model, Is.EqualTo("gpt-4o-mini"));
                Assert.That(loaded.ApiKey, Is.EqualTo("sk-secret-value"));
                Assert.That(loaded.TimeoutSeconds, Is.EqualTo(45));
                Assert.That(loaded.MaxDiffLines, Is.EqualTo(200));
                Assert.That(loaded.ConsentedHost, Is.EqualTo("llm.example.com"));
            });
        }

        [Test]
        public void Save_WritesNoPlainTextKey_WhenApiKeyGiven()
        {
            var store = new AiSettingsStore(_path, new ReversibleProtector());

            store.Save(new AiSettings { BaseUrl = "https://x/v1", Model = "m", ApiKey = "sk-secret-value" });

            // 이 기능에서 가장 값진 단언이다. 한번 깨지면 조용히 깨진다.
            Assert.That(File.ReadAllText(_path), Does.Not.Contain("sk-secret-value"));
        }

        [Test]
        public void Load_ReturnsDefaults_WhenFileMissing()
        {
            var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

            Assert.Multiple(() =>
            {
                Assert.That(loaded.BaseUrl, Is.Empty);
                Assert.That(loaded.TimeoutSeconds, Is.EqualTo(30));
                Assert.That(loaded.MaxDiffLines, Is.EqualTo(400));
                Assert.That(loaded.IsConfigured, Is.False);
            });
        }

        [Test]
        public void Load_ReturnsEmptyKey_WhenProtectedValueCannotBeRead()
        {
            // 다른 계정이 암호화한 파일을 받은 경우다. 예외로 죽으면 화면 전체가 열리지 않는다.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{\"baseUrl\":\"https://x/v1\",\"model\":\"m\",\"protectedApiKey\":\"garbage\"}");

            var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

            Assert.Multiple(() =>
            {
                Assert.That(loaded.ApiKey, Is.Empty);
                Assert.That(loaded.BaseUrl, Is.EqualTo("https://x/v1"));
            });
        }

        [Test]
        public void Load_ReturnsDefaults_WhenFileIsLockedByAnotherProcess()
        {
            // UnauthorizedAccessException/IOException 계열은 JsonException이 아니다 —
            // 다른 프로세스가 파일을 배타적으로 잠그고 있을 때도 예외로 죽으면 안 된다.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{\"baseUrl\":\"https://x/v1\",\"model\":\"m\"}");

            using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

                Assert.Multiple(() =>
                {
                    Assert.That(loaded.BaseUrl, Is.Empty);
                    Assert.That(loaded.TimeoutSeconds, Is.EqualTo(30));
                    Assert.That(loaded.MaxDiffLines, Is.EqualTo(400));
                    Assert.That(loaded.IsConfigured, Is.False);
                });
            }
        }

        [Test]
        public void Load_ReturnsDefaults_WhenFileAccessIsDenied()
        {
            // 디렉터리를 경로에 두는 방법은 시도했으나 기각했다 — 이 .NET에서는
            // File.Exists가 디렉터리에 대해 false를 반환해서 AiSettingsStore.Load()의
            // 첫 줄(File.Exists 가드)에서 바로 반환하고, try/catch 자체를 타지 않는다.
            // 그래서 그 경로로는 좁은 catch든 넓은 catch든 항상 통과해 버려 아무것도
            // 증명하지 못한다. 실제로 File.ReadAllText가 UnauthorizedAccessException을
            // 던지게 하려면 파일은 존재해야(File.Exists = true) 한다 — 그래서 파일
            // 자신에게 현재 계정의 읽기 권한을 명시적으로 거부하는 Deny ACE를 건다.
            // 소유자는 관리자 권한 없이도 자신의 파일에 ACL을 걸 수 있다.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, "{\"baseUrl\":\"https://x/v1\",\"model\":\"m\"}");

            var currentUser = WindowsIdentity.GetCurrent().User!;
            var fileInfo = new FileInfo(_path);
            var denyRule = new FileSystemAccessRule(currentUser, FileSystemRights.ReadData, AccessControlType.Deny);
            var acl = fileInfo.GetAccessControl();
            acl.AddAccessRule(denyRule);
            fileInfo.SetAccessControl(acl);

            try
            {
                var loaded = new AiSettingsStore(_path, new ReversibleProtector()).Load();

                Assert.Multiple(() =>
                {
                    Assert.That(loaded.BaseUrl, Is.Empty);
                    Assert.That(loaded.TimeoutSeconds, Is.EqualTo(30));
                    Assert.That(loaded.MaxDiffLines, Is.EqualTo(400));
                    Assert.That(loaded.IsConfigured, Is.False);
                });
            }
            finally
            {
                // Deny 규칙을 지워야 TearDown의 Directory.Delete가 성공한다.
                var cleanupAcl = fileInfo.GetAccessControl();
                cleanupAcl.RemoveAccessRule(denyRule);
                fileInfo.SetAccessControl(cleanupAcl);
            }
        }

        [Test]
        public void IsConfigured_ReturnsTrue_WhenBaseUrlAndModelPresent()
        {
            // 키는 필수가 아니다 — 사내 LLM 서버는 인증 없이 열려 있는 경우가 있다.
            var settings = new AiSettings { BaseUrl = "https://x/v1", Model = "m" };

            Assert.That(settings.IsConfigured, Is.True);
        }
    }
}
