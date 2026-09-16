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
        /// 주소에서 목적지(스킴+호스트+포트)를 뽑는다. 해석하지 못하면 원문을 그대로 돌려준다 —
        /// 빈 문자열을 돌려주면 동의를 묻지 않고 전송하는 경로가 생긴다.
        ///
        /// 호스트만 같으면 같은 목적지로 보는 것은 틀렸다 — HTTPS로 동의를 받은 뒤 같은
        /// 호스트가 평문 HTTP로 바뀌어도 다시 묻지 않으면 운영 DDL이 그대로 새어 나간다.
        /// <see cref="Uri.GetLeftPart"/>가 기본 포트(https의 443 등)를 생략해 주므로, 저장해
        /// 둔 값과 비교할 값의 "기본 포트 표기 여부"가 항상 같다 — 그래서 스킴에 맞는
        /// 기본 포트로 명시했던 주소는 다시 묻지 않는다.
        /// </summary>
        public static string DestinationOf(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.GetLeftPart(UriPartial.Authority)
                : baseUrl!.Trim();
        }

        /// <summary>이 설정의 목적지로 보내기 전에 사용자에게 물어야 하는지.</summary>
        public static bool NeedsConsent(AiSettings settings)
        {
            if (settings == null) return true;

            var destination = DestinationOf(settings.BaseUrl);
            return !string.Equals(destination, settings.ConsentedHost, StringComparison.OrdinalIgnoreCase);
        }
    }
}
