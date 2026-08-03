using System;

namespace DBVC.Core
{
    /// <summary>원격 URL의 종류. 안내를 붙일지 말지의 유일한 근거다.</summary>
    internal enum RemoteUrlKind
    {
        /// <summary>ssh:// 스킴 또는 scp 형식(git@host:path).</summary>
        Ssh,
        Https,
        /// <summary>로컬 경로, UNC, file:// 등 인증이 필요 없는 원격.</summary>
        Other,
        /// <summary>비었거나 인식하지 못하는 형태.</summary>
        Unknown
    }

    /// <summary>
    /// Pull 실패를 사용자가 행동할 수 있는 한국어 안내로 옮긴다.
    /// 순수 함수만 두어 네트워크 없이 전량 단위 테스트한다.
    /// </summary>
    internal static class RemoteDiagnostics
    {
        internal static RemoteUrlKind Classify(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) return RemoteUrlKind.Unknown;

            var url = remoteUrl!.Trim();

            if (StartsWith(url, "ssh://") || StartsWith(url, "git+ssh://")) return RemoteUrlKind.Ssh;
            if (StartsWith(url, "https://") || StartsWith(url, "http://")) return RemoteUrlKind.Https;
            if (StartsWith(url, "file://")) return RemoteUrlKind.Other;

            // UNC와 유닉스 절대 경로.
            if (url.StartsWith(@"\\", StringComparison.Ordinal) || url[0] == '/') return RemoteUrlKind.Other;

            var colon = url.IndexOf(':');
            if (colon <= 0) return RemoteUrlKind.Unknown;

            var host = url.Substring(0, colon);

            // 'C:\repos\x' 같은 드라이브 문자. scp 형식의 호스트는 한 글자일 수 없다.
            if (host.Length == 1) return RemoteUrlKind.Other;

            // scp 형식의 호스트 부분에는 경로 구분자가 없다.
            if (host.IndexOf('/') >= 0 || host.IndexOf('\\') >= 0) return RemoteUrlKind.Unknown;

            // 'git://host/path'처럼 인식하지 못하는 스킴의 URI 형태. scp 형식은 콜론 다음이 '//'로 시작하지 않는다.
            if (url.Substring(colon + 1).StartsWith("//", StringComparison.Ordinal)) return RemoteUrlKind.Unknown;

            return RemoteUrlKind.Ssh;
        }

        private static bool StartsWith(string value, string prefix)
        {
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
