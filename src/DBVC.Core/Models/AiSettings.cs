namespace DBVC.Core.Models
{
    /// <summary>
    /// AI 커밋 메시지 생성에 필요한 설정. 디스크 표현은 <see cref="DBVC.Core.AiSettingsStore"/>가 정한다.
    /// </summary>
    public class AiSettings
    {
        /// <summary>
        /// 설정 파일이 없을 때 쓰는 사내 LLM 서버 주소.
        ///
        /// 기본값을 두는 이유는 도입 비용이다 — 개발자 20명이 각자 주소를 받아 적어 넣게 하면
        /// 그 단계에서 대부분 멈춘다. <b>기본값은 목적지를 정해 줄 뿐 동의를 대신하지 않는다</b>:
        /// <see cref="ConsentedHost"/>는 비어 있으므로 첫 전송 전에 이 주소를 보여 주고 묻는다.
        ///
        /// 끝에 <c>/v1</c>이 없는 것은 오타가 아니다. 이 서버는 앞단 프록시가
        /// <c>/chat/completions</c>를 그대로 받는다 — 붙이는 경로는
        /// <see cref="DBVC.Core.OpenAiCompatibleClient"/>가 정한다.
        /// </summary>
        public const string DefaultBaseUrl = "http://172.20.100.40";

        /// <summary>기본 모델. 사내 서버가 올려 둔 것에 맞춘 값이다.</summary>
        public const string DefaultModel = "pelly:latest";

        /// <summary>OpenAI 호환 엔드포인트의 기준 URL. 예: <c>https://api.openai.com/v1</c></summary>
        public string BaseUrl { get; set; } = DefaultBaseUrl;

        public string Model { get; set; } = DefaultModel;

        /// <summary>평문. 디스크에는 보호된 형태로만 닿는다.</summary>
        public string ApiKey { get; set; } = string.Empty;

        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>AI에게 보내는 diff의 최대 줄 수. 토큰 비용이 아니라 전송량 자체의 한계다.</summary>
        public int MaxDiffLines { get; set; } = 400;

        /// <summary>
        /// 사용자가 전송에 동의한 호스트. 값이 다르면 다시 묻는다 —
        /// 동의는 "AI를 쓰는 것"이 아니라 "이 목적지로 보내는 것"에 대한 것이다.
        /// </summary>
        public string? ConsentedHost { get; set; }

        /// <summary>
        /// 호출을 시도해 볼 수 있는 상태인지. API 키는 조건이 아니다 —
        /// 사내 LLM 서버는 인증 없이 열려 있는 경우가 있고, 그때 키를 요구하면 쓸 수 없다.
        /// </summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
    }
}
