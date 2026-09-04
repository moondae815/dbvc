using System;
using System.Diagnostics;
using System.IO;
using LibGit2Sharp;

namespace DBVC.Core
{
    /// <summary>저장소의 커밋 작성자 신원 상태.</summary>
    public enum GitIdentityState
    {
        /// <summary>판정할 근거가 없다. 경로가 없거나 Git 저장소가 아니다.</summary>
        Unknown,

        /// <summary>user.name 또는 user.email이 없다. 커밋하면 누가 바꿨는지 남지 않는다.</summary>
        Missing,

        /// <summary>로컬·전역·시스템 어딘가에 신원이 있다.</summary>
        Configured
    }

    /// <summary>
    /// 커밋 작성자 신원을 판정하고 저장소 로컬 config에 쓴다.
    ///
    /// 판정 규칙이 화면과 Core 두 곳에 생기면 갈라지고, 갈라진 날 배너 없이 차단되는 사람이
    /// 나온다. 그래서 규칙은 <see cref="IsConfigured"/> 하나뿐이고 나머지는 그것을 부른다.
    /// </summary>
    public static class GitIdentity
    {
        /// <summary>
        /// 저장소를 열어 신원 유무를 본다.
        ///
        /// 판정하지 못하면 <see cref="GitIdentityState.Unknown"/>이다. 판정할 수 없는 경로를
        /// "없음"으로 뭉개면 배너가 상시로 뜬다 - <see cref="RepositoryEncoding.Detect"/>가
        /// Unknown을 두는 이유와 같다.
        /// </summary>
        public static GitIdentityState Detect(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return GitIdentityState.Unknown;

            try
            {
                if (!Directory.Exists(repoPath) || !Repository.IsValid(repoPath))
                {
                    return GitIdentityState.Unknown;
                }

                using var repo = new Repository(repoPath);
                return IsConfigured(repo) ? GitIdentityState.Configured : GitIdentityState.Missing;
            }
            catch (Exception ex)
            {
                // 판정 실패로 접속 전체가 실패하면 배너 하나 때문에 화면을 잃는다.
                Debug.WriteLine($"GitIdentity.Detect failed for '{repoPath}': {ex.Message}");
                return GitIdentityState.Unknown;
            }
        }

        /// <summary>
        /// 이미 열린 저장소에 신원이 있는지. 규칙의 유일한 자리다.
        ///
        /// BuildSignature는 user.name·user.email 중 하나라도 없으면 null을 낸다. 찾는 순서는
        /// git과 같은 로컬 → 전역 → 시스템이므로, 전역에만 설정해 둔 사람도 여기서 참이 된다.
        /// </summary>
        public static bool IsConfigured(Repository repo)
        {
            if (repo == null) return false;
            return repo.Config.BuildSignature(DateTimeOffset.Now) != null;
        }

        /// <summary>
        /// 신원을 저장소 로컬 config(.git/config)에 쓴다.
        ///
        /// 전역에 쓰지 않는다. SSMS 확장이 개발자의 다른 프로젝트 커밋 작성자까지 바꾸는 것은
        /// 되돌리는 길이 도구 안에 없는 부작용이다.
        /// </summary>
        public static void Write(string repoPath, string name, string email)
        {
            using var repo = new Repository(repoPath);
            repo.Config.Set("user.name", (name ?? string.Empty).Trim(), ConfigurationLevel.Local);
            repo.Config.Set("user.email", (email ?? string.Empty).Trim(), ConfigurationLevel.Local);
        }

        /// <summary>
        /// 입력이 쓸 수 있는 것인지 본다. 통과면 null, 아니면 사용자에게 보일 한국어 사유.
        ///
        /// 형식까지만 본다. 메일이 GitLab 계정과 실제로 일치하는지는 확인할 수 없다 -
        /// 폐쇄망 GitLab API를 부르는 것은 이 기능의 범위 밖이다.
        /// </summary>
        public static string? Validate(string? name, string? email)
        {
            var trimmedName = (name ?? string.Empty).Trim();
            if (trimmedName.Length == 0) return "이름을 입력하세요.";

            // 줄바꿈은 Trim으로 걸러지지 않는다(양 끝이 아니라 중간에 있으므로). 그대로
            // .git/config와 커밋 객체 헤더에 들어가면 git이 읽을 수 없는 값이 된다.
            if (trimmedName.IndexOf('\r') >= 0 || trimmedName.IndexOf('\n') >= 0)
            {
                return "이름에 줄바꿈이 들어갈 수 없습니다.";
            }

            var trimmed = (email ?? string.Empty).Trim();
            if (trimmed.Length == 0) return "메일 주소를 입력하세요.";

            var at = trimmed.IndexOf('@');
            if (at <= 0 || at == trimmed.Length - 1)
            {
                return "메일 주소 형식이 올바르지 않습니다. 예: hong@corp.co.kr";
            }

            var domain = trimmed.Substring(at + 1);
            if (!domain.Contains(".") || domain.StartsWith(".") || domain.EndsWith("."))
            {
                return "메일 주소 형식이 올바르지 않습니다. 예: hong@corp.co.kr";
            }

            // 공백·줄바꿈이 든 주소는 git이 받아 주더라도 이력에서 사람을 찾을 수 없게 만든다.
            if (trimmed.IndexOf(' ') >= 0 || trimmed.IndexOf('\t') >= 0
                || trimmed.IndexOf('\r') >= 0 || trimmed.IndexOf('\n') >= 0)
            {
                return "메일 주소에 공백이 들어갈 수 없습니다.";
            }

            return null;
        }
    }
}
