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
        private static readonly string HttpsGuidance = string.Join(Environment.NewLine, new[]
        {
            "HTTPS 원격은 DBVC가 인증할 수 없습니다. SSH 원격으로 바꾸세요.",
            "예: https://github.com/org/repo.git -> git@github.com:org/repo.git",
            "Git 클라이언트에서 'git remote set-url origin <SSH URL>'을 실행하면 됩니다."
        });

        private static readonly string SshMissingGuidance = string.Join(Environment.NewLine, new[]
        {
            "SSH 원격이지만 ssh 실행 파일을 찾을 수 없습니다.",
            // 경로는 Windows 11 기준이다 (Windows 10에서는 '앱 > 선택적 기능'이었다).
            "Windows 설정 > 시스템 > 선택적 기능에서 'OpenSSH 클라이언트'를 설치한 뒤 다시 시도하세요."
        });

        private static readonly string SshFailureGuidance = string.Join(Environment.NewLine, new[]
        {
            "원격과 통신하지 못했다면 다음을 확인하세요.",
            "- 공개키가 원격 계정에 등록되어 있는지",
            "- 해당 호스트가 known_hosts에 등록되어 있는지 (Git 클라이언트에서 한 번 접속해 두세요)",
            "- 원격 호스트의 SSH 포트(기본 22)가 열려 있는지"
        });

        /// <summary>
        /// 안내할 것이 있으면 한국어 문구를, 없으면 <c>null</c>을 반환한다.
        /// <c>null</c>일 때 호출자는 원본 오류 메시지를 그대로 둔다 - 근거 없는 추측을 덧붙이지 않는다.
        /// </summary>
        internal static string? Explain(string? remoteUrl, bool sshExecutableAvailable)
        {
            switch (Classify(remoteUrl))
            {
                case RemoteUrlKind.Https:
                    return HttpsGuidance;
                case RemoteUrlKind.Ssh:
                    return sshExecutableAvailable ? SshFailureGuidance : SshMissingGuidance;
                default:
                    return null;
            }
        }

        internal static RemoteUrlKind Classify(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) return RemoteUrlKind.Unknown;

            var url = remoteUrl!.Trim();

            if (StartsWith(url, "ssh://") || StartsWith(url, "git+ssh://") || StartsWith(url, "ssh+git://")) return RemoteUrlKind.Ssh;
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
