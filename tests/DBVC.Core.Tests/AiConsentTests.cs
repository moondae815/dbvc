using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class AiConsentTests
    {
        [Test]
        public void HostOf_ReturnsHost_WhenUrlIsAbsolute()
        {
            Assert.That(AiConsent.HostOf("https://api.openai.com/v1"), Is.EqualTo("api.openai.com"));
        }

        [Test]
        public void HostOf_ReturnsRawValue_WhenUrlIsNotParsable()
        {
            // 판정이 조용히 빈 문자열이 되면 동의를 묻지 않고 전송해 버린다.
            Assert.That(AiConsent.HostOf("llm.example.com/v1"), Is.EqualTo("llm.example.com/v1"));
        }

        [Test]
        public void NeedsConsent_ReturnsTrue_WhenNoConsentRecorded()
        {
            var settings = new AiSettings { BaseUrl = "https://api.openai.com/v1", Model = "m" };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }

        [Test]
        public void NeedsConsent_ReturnsFalse_WhenHostAlreadyConsented()
        {
            var settings = new AiSettings
            {
                BaseUrl = "https://api.openai.com/v1",
                Model = "m",
                ConsentedHost = "api.openai.com",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.False);
        }

        [Test]
        public void NeedsConsent_ReturnsTrue_WhenHostChanged()
        {
            // 동의는 "AI를 쓰는 것"이 아니라 "이 목적지로 보내는 것"에 대한 것이다.
            var settings = new AiSettings
            {
                BaseUrl = "https://other-llm.example.com/v1",
                Model = "m",
                ConsentedHost = "api.openai.com",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }
    }
}
