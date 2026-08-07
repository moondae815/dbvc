using System;

namespace DBVC.Core
{
    /// <summary>
    /// SQL 인증으로 설정되어 있으나 접속에 쓸 암호를 확보할 수 없을 때 던진다.
    /// 자격증명은 프로세스 메모리에만 있고 이번 세션에서 SSMS 개체 탐색기가 암호를
    /// 넘겨주지 않은 경우다 — 디스크에 저장된 값이 없으므로 폴백은 없다.
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
