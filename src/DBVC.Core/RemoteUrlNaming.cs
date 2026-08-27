using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격 주소에서 받을 폴더 이름을 제안한다. 순수 함수만 두어 네트워크 없이 전량 테스트한다.
    ///
    /// 제안일 뿐 강제가 아니다 — 사용자가 대화상자에서 고칠 수 있다. 그래서 판정하지 못하는
    /// 입력에는 억지로 이름을 만들어 내지 않고 null을 돌려준다.
    /// </summary>
    public static class RemoteUrlNaming
    {
        private const string GitSuffix = ".git";

        /// <summary>
        /// Windows가 폴더 이름에 허용하지 않는 문자.
        ///
        /// <see cref="System.IO.Path.GetInvalidFileNameChars"/>를 쓰지 않는다 — 그 값은 실행 중인 OS가
        /// 정해서 Unix에서는 '\0'과 '/'만 돌려준다. 같은 입력이 플랫폼마다 다른 답을 내면
        /// Linux에서 도는 CI가 Windows에서 통과한 테스트를 떨어뜨린다. DBVC가 실제로 도는
        /// 곳은 언제나 Windows이므로 그쪽 규칙을 고정해 박는다.
        /// </summary>
        private static readonly char[] InvalidFolderNameChars =
            { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        public static string? SuggestFolderName(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) return null;

            var trimmed = remoteUrl!.Trim().TrimEnd('/', '\\');
            if (trimmed.Length == 0) return null;

            // scp 형식(git@host:org/name)은 콜론이, URL 형식은 슬래시가 마지막 구분자다.
            // 둘을 함께 보면 형식을 먼저 판정하지 않아도 된다.
            var cut = trimmed.LastIndexOfAny(new[] { '/', '\\', ':' });
            var name = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;

            if (name.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - GitSuffix.Length);
            }

            if (name.Length == 0) return null;

            // 못 만들 이름을 제안하면 사용자가 확인을 누른 뒤에야 실패한다.
            if (name.IndexOfAny(InvalidFolderNameChars) >= 0) return null;

            return name;
        }
    }
}
