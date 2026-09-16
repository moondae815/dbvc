using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using DBVC.Core;
using DBVC.Core.Models;

namespace DBVC.Core.Tests
{
    [TestFixture]
    public class OpenAiCompatibleClientTests
    {
        /// <summary>요청을 기록하고 정해진 응답을 돌려준다. 네트워크를 타지 않는다.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;

            public StubHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
            }
        }

        private const string SuccessBody =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"feat: 주문 뷰를 더한다\"}}]}";

        private static AiSettings Settings(string baseUrl = "https://llm.example.com/v1", string apiKey = "sk-test") =>
            new AiSettings { BaseUrl = baseUrl, Model = "test-model", ApiKey = apiKey, TimeoutSeconds = 30 };

        [Test]
        public async Task CompleteAsync_ReturnsContent_WhenResponseIsSuccessful()
        {
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            var result = await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None);

            Assert.That(result, Is.EqualTo("feat: 주문 뷰를 더한다"));
        }

        [Test]
        public async Task CompleteAsync_PostsToChatCompletions_WhenBaseUrlHasTrailingSlash()
        {
            // 사용자가 끝에 슬래시를 붙여 넣는 일은 흔하다. 두 형태가 같은 URL이 되어야 한다.
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings("https://llm.example.com/v1/"), "sys", "user", CancellationToken.None);

            Assert.That(
                handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("https://llm.example.com/v1/chat/completions"));
        }

        [Test]
        public async Task CompleteAsync_SendsBearerHeader_WhenApiKeyPresent()
        {
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None);

            Assert.That(handler.LastRequest!.Headers.Authorization!.ToString(), Is.EqualTo("Bearer sk-test"));
        }

        [Test]
        public async Task CompleteAsync_OmitsAuthorizationHeader_WhenApiKeyEmpty()
        {
            // 사내 LLM 서버는 인증 없이 열려 있는 경우가 있다. 빈 Bearer를 보내면 거부하는 구현이 있다.
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings(apiKey: string.Empty), "sys", "user", CancellationToken.None);

            Assert.That(handler.LastRequest!.Headers.Authorization, Is.Null);
        }

        [Test]
        public async Task CompleteAsync_SendsModelAndMessages_WhenCalled()
        {
            var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);
            var client = new OpenAiCompatibleClient(handler);

            await client.CompleteAsync(Settings(), "시스템 지시", "사용자 내용", CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(handler.LastBody, Does.Contain("\"model\":\"test-model\""));
                Assert.That(handler.LastBody, Does.Contain("시스템 지시"));
                Assert.That(handler.LastBody, Does.Contain("사용자 내용"));
            });
        }

        [TestCase(HttpStatusCode.Unauthorized, "API 키")]
        [TestCase(HttpStatusCode.Forbidden, "API 키")]
        [TestCase(HttpStatusCode.NotFound, "주소")]
        [TestCase((HttpStatusCode)429, "한도")]
        [TestCase(HttpStatusCode.InternalServerError, "응답하지 못했습니다")]
        public void CompleteAsync_ThrowsKoreanReason_WhenStatusIsError(HttpStatusCode status, string expectedFragment)
        {
            var client = new OpenAiCompatibleClient(new StubHandler(status, "{\"error\":\"nope\"}"));

            var ex = Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain(expectedFragment));
        }

        [Test]
        public void CompleteAsync_Throws_WhenResponseHasNoChoices()
        {
            var client = new OpenAiCompatibleClient(new StubHandler(HttpStatusCode.OK, "{\"choices\":[]}"));

            var ex = Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("응답"));
        }

        // FIX 1: 200 본문이 유효한 JSON이지만 예상한 모양(객체, choices 배열)이 아닐 때
        // TryGetProperty/GetArrayLength가 InvalidOperationException을 던지던 것을 막는다.
        [TestCase("\"hello\"")]
        [TestCase("[1,2,3]")]
        [TestCase("42")]
        public void CompleteAsync_ThrowsAiRequestException_WhenBodyIsNotJsonObject(string body)
        {
            var client = new OpenAiCompatibleClient(new StubHandler(HttpStatusCode.OK, body));

            Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));
        }

        [Test]
        public void CompleteAsync_ThrowsAiRequestException_WhenChoicesIsNotArray()
        {
            var client = new OpenAiCompatibleClient(new StubHandler(HttpStatusCode.OK, "{\"choices\":\"oops\"}"));

            Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));
        }

        // FIX 2: 게이트웨이가 Authorization 헤더를 오류 본문에 그대로 반사하는 경우, 그 본문을
        // 인용하는 예외 메시지에 API 키가 그대로 남아서는 안 된다.
        [Test]
        public void CompleteAsync_RedactsApiKey_WhenServerEchoesItInErrorBody()
        {
            var client = new OpenAiCompatibleClient(
                new StubHandler(HttpStatusCode.InternalServerError, "{\"error\":\"rejected token sk-test\"}"));

            var ex = Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));

            Assert.That(ex!.Message, Does.Not.Contain("sk-test"));
        }

        [Test]
        public void CompleteAsync_LeavesErrorBodyIntact_WhenApiKeyEmpty()
        {
            // 빈 문자열을 Replace의 old value로 쓰면 모든 문자 사이에 마스크가 끼어 본문이
            // 깨진다. 키가 없을 때는 치환 자체를 건너뛰어야 한다.
            var client = new OpenAiCompatibleClient(
                new StubHandler(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}"));

            var ex = Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(apiKey: string.Empty), "sys", "user", CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("boom"));
        }

        // FIX 3: content가 null이면 choices가 비어 있을 때와 같은 취급이어야 한다 —
        // "내용을 돌려주거나, 한국어 사유로 던지거나" 둘 중 하나만 허용한다.
        [Test]
        public void CompleteAsync_Throws_WhenContentIsNull()
        {
            var client = new OpenAiCompatibleClient(new StubHandler(
                HttpStatusCode.OK,
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null}}]}"));

            var ex = Assert.ThrowsAsync<AiRequestException>(async () =>
                await client.CompleteAsync(Settings(), "sys", "user", CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("응답"));
        }
    }
}
