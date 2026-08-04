using System;

namespace DBVC.Core
{
    /// <summary>
    /// SQL 인증으로 설정되어 있으나 접속에 쓸 암호를 확보할 수 없을 때 던진다.
    /// 저장된 암호가 없거나, 다른 Windows 계정에서 보호된 값이라 복호화하지 못한 경우다.
    /// </summary>
    public class SqlCredentialException : Exception
    {
        public SqlCredentialException(string message) : base(message)
        {
        }

        public SqlCredentialException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
