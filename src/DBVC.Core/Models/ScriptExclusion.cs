namespace DBVC.Core.Models
{
    /// <summary>
    /// 배포 스크립트에서 객체가 빠진 이유. 셋 다 사용자가 할 일이 다르므로 뭉뚱그리지 않는다.
    /// 열거 순서가 곧 헤더에 적히는 순서다.
    /// </summary>
    public enum ScriptExclusionReason
    {
        /// <summary>스크립트로 만들 내용이 없다. 파일이 없거나 비었다.</summary>
        NoContent,

        /// <summary>
        /// 대상에 이미 있는데 <c>CREATE OR ALTER</c>를 지원하지 않는 타입이다.
        /// 그대로 실행하면 "이미 있습니다"로 실패한다.
        /// </summary>
        ManualChangeRequired,

        /// <summary>DB에만 있고 브랜치에 없다. 스크립트에 담을 재료 자체가 없다.</summary>
        NotInBranch,

        /// <summary>
        /// 스크립팅에 실패해 차이 자체를 판정하지 못했다. 차이가 아니라 "모른다"이므로
        /// 스크립트에도 들어가지 않는다.
        ///
        /// 셋과 성격이 다른데도 같은 자리에 적는 이유는, 나중에 이 <c>.sql</c>만 열어 보는
        /// DBA에게 문서가 비교 전체를 덮는다고 암묵적으로 주장하기 때문이다. 화면의 알림은
        /// 파일과 함께 남지 않는다.
        /// </summary>
        NotCompared
    }

    public class ScriptExclusion
    {
        public ScriptExclusion(string qualifiedName, ScriptExclusionReason reason)
        {
            QualifiedName = qualifiedName;
            Reason = reason;
        }

        public string QualifiedName { get; }
        public ScriptExclusionReason Reason { get; }
    }
}
