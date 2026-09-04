using System;

namespace DBVC.Core
{
    /// <summary>
    /// 커밋 작성자 신원(user.name·user.email)이 없어 커밋 또는 병합을 거부했다.
    /// 저장소는 손대지 않았다 - 스테이징도 하지 않는다.
    ///
    /// <see cref="InvalidOperationException"/>을 물려받는 이유는
    /// <see cref="GitRemoteNotConfiguredException"/>과 같다. 화면을 거치지 않는 호출자도
    /// 지금까지 이 타입으로 안내를 받아 왔고, 그 동작이 옳다.
    ///
    /// 예전에는 이 자리에서 "DBVC User &lt;dbvc@example.com&gt;"으로 커밋했다. 20명이 한
    /// 저장소를 쓰면 blame과 MR 작성자가 전부 같은 이름이 되고, 되돌리려면 이력을 다시 써야
    /// 한다. 폴백을 되살리지 말 것.
    /// </summary>
    public class GitIdentityMissingException : InvalidOperationException
    {
        public const string UserMessage =
            "커밋 작성자가 설정되어 있지 않습니다. 이름과 메일 주소를 먼저 지정하세요.";

        public GitIdentityMissingException() : base(UserMessage)
        {
        }
    }
}
