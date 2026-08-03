using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격이 사용자 자격 증명(사용자명/암호·토큰)을 요구했으나 DBVC는 Windows 통합 인증만 지원한다.
    /// </summary>
    public class GitAuthenticationException : Exception
    {
        public GitAuthenticationException(string message) : base(message)
        {
        }

        public GitAuthenticationException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
