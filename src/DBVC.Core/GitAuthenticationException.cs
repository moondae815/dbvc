using System;

namespace DBVC.Core
{
    /// <summary>
    /// HTTPS 원격이 사용자 자격 증명을 요구했으나 DBVC가 제공할 수 없다.
    /// DBVC는 인증을 SSH에 위임하며 비밀을 보관하지 않는다.
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
