using System;
using System.IO;

namespace DBVC.Core
{
    /// <summary>
    /// libgit2는 SSH를 자체 구현하지 않고 시스템 <c>ssh</c> 실행 파일에 위임한다(ssh_exec 전송).
    /// 실행 파일이 없으면 SSH 원격 Pull은 원인을 알 수 없는 오류로 실패하므로, 그 경우를 먼저 가려낸다.
    /// </summary>
    internal static class SshExecutableLocator
    {
        internal static bool IsAvailable()
        {
            return IsAvailable(
                Environment.GetEnvironmentVariable("GIT_SSH_COMMAND"),
                Environment.GetEnvironmentVariable("GIT_SSH"),
                Environment.GetEnvironmentVariable("PATH"),
                File.Exists);
        }

        /// <summary>
        /// 실제 판정. <paramref name="gitSshCommand"/>와 <paramref name="gitSsh"/>는 libgit2가 참조하는
        /// 환경 변수이므로 PATH 탐색보다 먼저 본다. 값의 내용은 검증하지 않는다 - 사용자가 설정했다면
        /// 그 판단을 따른다.
        /// </summary>
        internal static bool IsAvailable(
            string? gitSshCommand,
            string? gitSsh,
            string? pathVariable,
            Func<string, bool> fileExists)
        {
            if (!string.IsNullOrWhiteSpace(gitSshCommand)) return true;
            if (!string.IsNullOrWhiteSpace(gitSsh)) return true;
            if (string.IsNullOrWhiteSpace(pathVariable)) return false;

            foreach (var directory in pathVariable!.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                // Windows에서는 ssh.exe, 그 외에서는 ssh다. 둘 다 확인하면 플랫폼 분기가 필요 없다.
                if (fileExists(Path.Combine(directory, "ssh.exe"))) return true;
                if (fileExists(Path.Combine(directory, "ssh"))) return true;
            }

            return false;
        }
    }
}
