namespace DBVC.Core.Models
{
    /// <summary>
    /// Push의 결과. <c>bool</c>이 아닌 이유는 "올릴 커밋이 없다"가 실패가 아니기 때문이다 —
    /// 매핑 실패와 한 값으로 묶으면 호출자가 정상 상태를 오류로 보고하게 된다.
    /// </summary>
    public enum PushResult
    {
        /// <summary>이 (서버, 데이터베이스)에 매핑된 저장소가 없다.</summary>
        NoMapping,

        /// <summary>원격이 이미 최신이다. 정상 상태이며 오류가 아니다.</summary>
        NothingToPush,

        /// <summary>커밋을 원격에 올렸다.</summary>
        Pushed
    }
}
