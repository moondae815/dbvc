using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격과 통신하지 못해 Pull에 실패했고, 원인을 특정할 안내가 있는 경우.
    /// 메시지에 원본 오류와 한국어 안내가 함께 담긴다.
    /// <para>
    /// Vsix는 이 타입으로 분기하지 않는다. 의도적이다 - catch-all이 이미 제목 'DBVC Pull 실패'와
    /// <c>ex.Message</c>를 보여주므로, 전용 catch를 더하면 출력이 catch-all과 동일해져
    /// 아무것도 고정하지 못하는 테스트를 부른다.
    /// </para>
    /// </summary>
    public class GitRemoteException : Exception
    {
        public GitRemoteException(string message) : base(message)
        {
        }

        public GitRemoteException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
