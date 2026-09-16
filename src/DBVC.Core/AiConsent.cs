using System;
using DBVC.Core.Models;

namespace DBVC.Core
{
    /// <summary>
    /// "어디로 나가는지 모른 채 운영 DDL이 나가는 일"을 막는 판정.
    /// 확인 대화상자를 띄우는 것은 화면의 몫이고, 물어야 하는지는 여기가 정한다.
    /// </summary>
    public static class AiConsent
    {
        /// <summary>
        /// 주소에서 호스트만 뽑는다. 해석하지 못하면 원문을 그대로 돌려준다 —
        /// 빈 문자열을 돌려주면 동의를 묻지 않고 전송하는 경로가 생긴다.
        /// </summary>
        public static string HostOf(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host
                : baseUrl!.Trim();
        }

        /// <summary>이 설정의 목적지로 보내기 전에 사용자에게 물어야 하는지.</summary>
        public static bool NeedsConsent(AiSettings settings)
        {
            if (settings == null) return true;

            var host = HostOf(settings.BaseUrl);
            return !string.Equals(host, settings.ConsentedHost, StringComparison.OrdinalIgnoreCase);
        }
    }
}
