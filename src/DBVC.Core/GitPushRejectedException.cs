using System;

namespace DBVC.Core
{
    /// <summary>
    /// 원격이 ref 갱신을 거부해 Push가 이루어지지 않았음을 알린다. (설계 4.3)
    ///
    /// 거부는 두 경로로 온다 - libgit2가 스스로 판정하는 <c>NonFastForwardException</c>과
    /// 서버가 상태로 보고하는 <c>OnPushStatusError</c>다. 사용자에게는 같은 일이므로
    /// 한 타입으로 수렴시킨다.
    /// </summary>
    public class GitPushRejectedException : Exception
    {
        public GitPushRejectedException(string message) : base(message)
        {
        }

        public GitPushRejectedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
