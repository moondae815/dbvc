using System;

namespace DBVC.Core
{
    /// <summary>
    /// 저장소에 원격이 없거나 현재 브랜치가 원격 브랜치를 추적하지 않아 원격 연산을 할 수
    /// 없는 경우. "원격과 통신하다 실패했다"(<see cref="GitRemoteException"/>)와는 다르다 —
    /// 통신을 시도조차 하지 않았고, 저장소는 손대지 않았다.
    ///
    /// <see cref="InvalidOperationException"/>을 그대로 물려받는다. Pull·Push·원격 확인 버튼은
    /// 지금까지 이 상황을 그 타입으로 받아 한국어 안내를 그대로 띄워 왔고, 그 동작이 옳다 —
    /// 사용자가 직접 누른 버튼이므로 조용히 넘기면 안 된다.
    ///
    /// 타입을 따로 두는 이유는 <b>사용자가 누르지 않은</b> Pull이 하나 있기 때문이다.
    /// 차이 검사는 낡은 브랜치로 비교하지 않으려고 Pull을 먼저 돌리는데(설계 3.1),
    /// 그때는 "원격이 없으면 건너뛰고, 원격이 있는데 실패하면 멈춘다"가 규칙이다.
    /// 원격 없이 폴더를 배포 클론으로 채택하는 것은 대화상자가 안내하는 정상 경로이므로,
    /// 그 경우에 패널의 유일한 버튼이 오류를 내면 화면 전체가 쓸모없어진다.
    /// </summary>
    public class GitRemoteNotConfiguredException : InvalidOperationException
    {
        public GitRemoteNotConfiguredException(string message) : base(message)
        {
        }
    }
}
