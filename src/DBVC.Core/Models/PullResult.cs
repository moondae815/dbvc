namespace DBVC.Core.Models
{
    /// <summary>
    /// Pull의 결과. 성공/실패 두 값으로는 "받을 것이 없었다"를 말할 수 없어
    /// 화면이 받은 것이 없는데 받았다고 안내하게 된다.
    /// </summary>
    public enum PullResult
    {
        /// <summary>이 (서버, 데이터베이스)에 매핑된 저장소가 없다.</summary>
        NoMapping,

        /// <summary>원격에 새 커밋이 없었다. 정상 상태이며 오류가 아니다.</summary>
        AlreadyUpToDate,

        /// <summary>원격의 커밋을 로컬에 반영했다.</summary>
        Pulled
    }
}
