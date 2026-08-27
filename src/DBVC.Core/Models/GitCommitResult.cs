namespace DBVC.Core.Models
{
    /// <summary>
    /// 커밋 시도의 결과. bool로는 "커밋할 것이 없다"와 "커밋할 수 없다"를 구분할 수 없는데,
    /// 그 둘은 뒤처리가 정반대다.
    ///
    /// 저장소가 이미 DB와 같아서 담을 것이 없는 경우(<see cref="NothingToCommit"/>)는 그 객체의
    /// DDL 로그 행을 닫아야 한다. 닫지 않으면 그 항목이 목록에 영원히 남는다 - 다시 커밋해도
    /// 또 차이가 없어 아무 일도 일어나지 않으므로 사용자가 지울 방법이 없다. 남이 만진 변경이
    /// 내 커밋에 이미 담겼을 때 이 상태가 된다.
    ///
    /// 반대로 매핑이 없거나 아무것도 고르지 않은 경우는 아무것도 건드리면 안 된다. 그때 로그를
    /// 닫으면 기록되지 않은 변경이 조용히 사라진다.
    /// </summary>
    public enum GitCommitResult
    {
        /// <summary>커밋이 만들어졌다.</summary>
        Committed,

        /// <summary>저장소가 이미 DB와 같아 담을 것이 없었다. 로그 행은 닫아야 한다.</summary>
        NothingToCommit,

        /// <summary>이 데이터베이스에 매핑된 저장소가 없다.</summary>
        NotMapped,

        /// <summary>호출자가 아무 경로도 넘기지 않았다.</summary>
        NothingSelected
    }
}
