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
        NotInBranch
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
