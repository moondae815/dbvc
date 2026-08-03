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
    }
}
