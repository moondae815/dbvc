using System;
using System.IO;

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
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

            return name;
        }
    }
}
