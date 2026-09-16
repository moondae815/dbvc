using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// AI 호출이 실패한 사유. 메시지는 그대로 화면에 뜨므로 한국어다.
    /// </summary>
    public class AiRequestException : Exception
    {
        public AiRequestException(string message) : base(message) { }
        public AiRequestException(string message, Exception inner) : base(message, inner) { }
    }

    public interface IChatCompletionClient
    {
        Task<string> CompleteAsync(AiSettings settings, string systemPrompt, string userMessage, CancellationToken cancellationToken);
    }

    /// <summary>
    /// OpenAI 호환 <c>/chat/completions</c>를 부른다. 사내 LLM 서버와 외부 상용 API가
    /// 같은 규약을 쓰므로 구현은 하나로 족하다.
    /// </summary>
    public class OpenAiCompatibleClient : IChatCompletionClient
    {
        private readonly HttpClient _httpClient;

        // \uXXXX 이스케이프도 유효한 JSON이라 규약을 지키는 서버라면 어느 쪽이든 같은 문자열로
        // 디코드한다 — 그래서 이 옵션은 동작을 고치는 게 아니다. 본문을 사람이 그대로 읽을 수
        // 있게 하고, 그 덕에 이 테스트 파일의 부분 문자열 단언들이 원문과 그대로 맞아떨어지게
        // 할 뿐이다.
        private static readonly JsonSerializerOptions PayloadOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <param name="handler">
        /// 테스트가 네트워크 없이 요청을 들여다보기 위한 이음매. 실제 실행에서는 null이다.
        /// </param>
        public OpenAiCompatibleClient(HttpMessageHandler? handler = null)
        {
            // HttpClient는 한 번 만들어 재사용한다. 호출마다 만들면 소켓이 고갈된다.
            _httpClient = handler == null ? new HttpClient() : new HttpClient(handler);
        }

        public async Task<string> CompleteAsync(
            AiSettings settings, string systemPrompt, string userMessage, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.BaseUrl));

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                // 빈 Bearer를 보내면 거부하는 구현이 있어, 키가 없으면 헤더 자체를 달지 않는다.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }

            var payload = new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage },
                },
                temperature = 0.2,
                max_tokens = 200,
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, PayloadOptions), Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30));

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiRequestException(
                    $"AI 서버가 {settings.TimeoutSeconds}초 안에 응답하지 않았습니다. 주소와 네트워크를 확인하세요.");
            }
            catch (HttpRequestException ex)
            {
                throw new AiRequestException(
                    $"AI 서버에 연결하지 못했습니다. 주소를 확인하세요.\n\n{ex.Message}", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new AiRequestException(DescribeFailure(response.StatusCode, body, settings.ApiKey));
                }

                return ExtractContent(body, settings.ApiKey);
            }
        }

        /// <summary>
        /// 끝 슬래시 유무와 무관하게 같은 URL이 되게 한다. 사용자가 붙여 넣는 값이라
        /// 두 형태가 모두 온다.
        /// </summary>
        private static string BuildEndpoint(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new AiRequestException("AI 프로바이더 주소가 설정되지 않았습니다.");
            }
            return baseUrl.TrimEnd('/') + "/chat/completions";
        }

        /// <summary>서버가 돌려준 영문 본문은 인용으로만 싣는다.</summary>
        private static string DescribeFailure(HttpStatusCode status, string body, string apiKey)
        {
            var reason = status switch
            {
                HttpStatusCode.Unauthorized => "AI 서버가 API 키를 거부했습니다.",
                HttpStatusCode.Forbidden => "AI 서버가 API 키의 권한을 거부했습니다.",
                HttpStatusCode.NotFound => "AI 서버에서 해당 주소를 찾지 못했습니다. 프로바이더 주소와 모델 이름을 확인하세요.",
                (HttpStatusCode)429 => "AI 서버의 호출 한도를 넘었습니다. 잠시 뒤에 다시 시도하세요.",
                _ => $"AI 서버가 요청에 응답하지 못했습니다 (HTTP {(int)status}).",
            };

            return string.IsNullOrWhiteSpace(body) ? reason : reason + "\n\n" + RedactAndShorten(body, apiKey);
        }

        /// <summary>
        /// 본문을 예외 메시지에 인용하기 전에 API 키를 지운다. 이 클라이언트는 키를 URL이나
        /// 로그에 직접 넣지 않지만, Authorization 헤더를 그대로 반사하는 게이트웨이라면 오류
        /// 본문에 키가 실려 돌아올 수 있고 그게 예외 메시지를 거쳐 화면·버그 리포트로 샌다.
        /// </summary>
        private static string RedactAndShorten(string body, string apiKey) =>
            Shorten(string.IsNullOrEmpty(apiKey) ? body : body.Replace(apiKey, "***"));

        private static string Shorten(string body) =>
            body.Length <= 500 ? body : body.Substring(0, 500) + "...";

        private static string ExtractContent(string body, string apiKey)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new AiRequestException(
                    $"AI 서버의 응답을 해석하지 못했습니다.\n\n{RedactAndShorten(body, apiKey)}", ex);
            }

            // TryGetProperty/GetArrayLength는 대상이 예상 종류(Object/Array)가 아니면
            // InvalidOperationException을 던진다 — 유효한 JSON이지만 모양이 다른 200 응답
            // (문자열·숫자·배열 루트, choices가 배열이 아닌 경우 등)에서 그 예외가 그대로
            // 화면까지 올라가지 않도록 각 단계에서 ValueKind를 먼저 확인한다.
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0
                    && choices[0].ValueKind == JsonValueKind.Object
                    && choices[0].TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString() ?? string.Empty;
                }
            }

            // content가 null인 경우도 여기로 떨어진다 — "내용을 돌려주거나 한국어 사유로
            // 던지거나" 둘 중 하나만 허용하므로, choices가 비어 있을 때와 같은 취급이다.
            throw new AiRequestException($"AI 서버가 빈 응답을 돌려주었습니다.\n\n{RedactAndShorten(body, apiKey)}");
        }
    }
}
