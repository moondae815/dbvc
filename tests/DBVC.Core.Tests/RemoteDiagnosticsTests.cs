using NUnit.Framework;
using DBVC.Core;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class RemoteDiagnosticsTests
    {
        // ---------- Classify ----------

        [TestCase("ssh://git@github.com/org/repo.git")]
        [TestCase("SSH://git@github.com/org/repo.git")]
        [TestCase("git+ssh://git@gitlab.corp.local/team/repo.git")]
        public void Classify_RecognizesSshScheme(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Ssh));
        }

        [TestCase("git@github.com:org/repo.git")]
        [TestCase("git@gitlab.corp.local:team/repo.git")]
        [TestCase("gitlab.corp.local:team/repo.git")]
        public void Classify_RecognizesScpForm(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Ssh),
                "scp 형식은 SSH입니다. 사내 GitLab에서 흔히 쓰는 형태입니다");
        }

        [TestCase("https://github.com/org/repo.git")]
        [TestCase("HTTPS://github.com/org/repo.git")]
        [TestCase("http://gitlab.corp.local/team/repo.git")]
        public void Classify_RecognizesHttpSchemes(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Https));
        }

        [TestCase(@"C:\repos\dbvc")]
        [TestCase(@"c:\repos\dbvc")]
        public void Classify_DoesNotMistakeAWindowsDriveLetterForScpForm(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Other),
                "드라이브 문자 뒤의 콜론을 scp 구분자로 읽으면 로컬 경로 원격에 SSH 안내가 붙습니다");
        }

        [TestCase("/home/user/repos/dbvc")]
        [TestCase(@"\\fileserver\share\repo")]
        [TestCase("file:///home/user/repo")]
        public void Classify_TreatsLocalAndUncPathsAsOther(string url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Other));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Classify_ReturnsUnknown_ForMissingUrl(string? url)
        {
            Assert.That(RemoteDiagnostics.Classify(url), Is.EqualTo(RemoteUrlKind.Unknown));
        }

        [Test]
        public void Classify_ReturnsUnknown_ForAnUnrecognizedForm()
        {
            Assert.That(RemoteDiagnostics.Classify("git://github.com/org/repo.git"),
                Is.EqualTo(RemoteUrlKind.Unknown),
                "인식하지 못하는 형태는 Unknown이어야 안내가 붙지 않습니다");
        }

        // ---------- Explain ----------

        [Test]
        public void Explain_TellsHttpsUsersToSwitchToSsh()
        {
            var guidance = RemoteDiagnostics.Explain("https://github.com/org/repo.git", sshExecutableAvailable: true);

            Assert.That(guidance, Is.Not.Null);
            Assert.That(guidance, Does.Contain("SSH 원격으로 바꾸세요"));
            Assert.That(guidance, Does.Contain("git remote set-url"),
                "사용자가 그대로 실행할 수 있는 명령을 줘야 합니다");
        }

        [Test]
        public void Explain_TellsTheUserToInstallOpenSsh_WhenTheSshExecutableIsMissing()
        {
            var guidance = RemoteDiagnostics.Explain("git@github.com:org/repo.git", sshExecutableAvailable: false);

            Assert.That(guidance, Does.Contain("OpenSSH 클라이언트"));
            Assert.That(guidance, Does.Not.Contain("known_hosts"),
                "실행 파일이 없는 단계에서 호스트 키를 확인하라는 안내는 순서가 틀립니다");
        }

        [Test]
        public void Explain_ListsTheThreeSshCauses_WhenTheExecutableIsPresent()
        {
            var guidance = RemoteDiagnostics.Explain("ssh://git@gitlab.corp.local/team/repo.git", sshExecutableAvailable: true);

            Assert.That(guidance, Does.Contain("공개키"));
            Assert.That(guidance, Does.Contain("known_hosts"));
            Assert.That(guidance, Does.Contain("22번 포트"));
        }

        [TestCase(@"C:\repos\dbvc")]
        [TestCase("/home/user/repos/dbvc")]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("git://github.com/org/repo.git")]
        public void Explain_ReturnsNull_WhenThereIsNoDeterministicCause(string? url)
        {
            Assert.That(RemoteDiagnostics.Explain(url, sshExecutableAvailable: true), Is.Null,
                "이것이 '무관한 실패에 힌트를 덧붙이지 않는다'는 계약입니다. 이 테스트가 깨지면 계약이 깨진 것입니다");
            Assert.That(RemoteDiagnostics.Explain(url, sshExecutableAvailable: false), Is.Null);
        }
    }
}
