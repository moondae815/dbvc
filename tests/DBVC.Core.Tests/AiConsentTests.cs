using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class AiConsentTests
    {
        [Test]
        public void DestinationOf_ReturnsOrigin_WhenUrlIsAbsolute()
        {
            Assert.That(AiConsent.DestinationOf("https://api.openai.com/v1"), Is.EqualTo("https://api.openai.com"));
        }

        [Test]
        public void DestinationOf_ReturnsRawValue_WhenUrlIsNotParsable()
        {
            // 판정이 조용히 빈 문자열이 되면 동의를 묻지 않고 전송해 버린다.
            Assert.That(AiConsent.DestinationOf("llm.example.com/v1"), Is.EqualTo("llm.example.com/v1"));
        }

        [Test]
        public void DestinationOf_OmitsPort_WhenPortIsSchemeDefault()
        {
            // 저장된 값과 비교할 값의 "기본 포트 표기 여부"가 늘 같아야, 이미 동의한
            // 목적지를 스킴 기본 포트로 다시 적었다는 이유로 또 묻는 일이 없다.
            Assert.That(AiConsent.DestinationOf("https://trusted.host:443/v1"), Is.EqualTo("https://trusted.host"));
        }

        [Test]
        public void DestinationOf_KeepsPort_WhenPortIsNotSchemeDefault()
        {
            Assert.That(AiConsent.DestinationOf("https://trusted.host:8443/v1"), Is.EqualTo("https://trusted.host:8443"));
        }

        [Test]
        public void NeedsConsent_ReturnsTrue_WhenNoConsentRecorded()
        {
            var settings = new AiSettings { BaseUrl = "https://api.openai.com/v1", Model = "m" };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }

        [Test]
        public void NeedsConsent_ReturnsFalse_WhenDestinationAlreadyConsented()
        {
            var settings = new AiSettings
            {
                BaseUrl = "https://api.openai.com/v1",
                Model = "m",
                ConsentedHost = "https://api.openai.com",
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
                ConsentedHost = "https://api.openai.com",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }

        [Test]
        public void NeedsConsent_ReturnsTrue_WhenSchemeChanged()
        {
            // HTTPS로 동의를 받은 뒤 같은 호스트가 평문 HTTP로 바뀌면, 호스트만 보는 판정은
            // 다시 묻지 않아 운영 DDL이 평문으로 새어 나간다 — 스킴도 목적지의 일부다.
            var settings = new AiSettings
            {
                BaseUrl = "http://trusted.host/v1",
                Model = "m",
                ConsentedHost = "https://trusted.host",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }

        [Test]
        public void NeedsConsent_ReturnsTrue_WhenPortChanged()
        {
            var settings = new AiSettings
            {
                BaseUrl = "https://trusted.host:8443/v1",
                Model = "m",
                ConsentedHost = "https://trusted.host",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.True);
        }

        [Test]
        public void NeedsConsent_ReturnsFalse_WhenSameOriginWithDifferentPathOrCase()
        {
            // 경로·대소문자가 달라도 목적지(스킴+호스트+포트)가 같으면 같은 목적지다 —
            // 이미 승인한 곳을 세부 표기 차이만으로 다시 묻지 않는다.
            var settings = new AiSettings
            {
                BaseUrl = "HTTPS://Trusted.Host:443/v2/chat",
                Model = "m",
                ConsentedHost = "https://trusted.host",
            };

            Assert.That(AiConsent.NeedsConsent(settings), Is.False);
        }
    }
}
