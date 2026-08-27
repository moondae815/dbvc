namespace DBVC.Core.Models
{
    /// <summary>
    /// 차단 사유. 값의 순서가 곧 우선순위다 — 여럿이 겹치면 작은 값을 알린다.
    /// </summary>
    public enum RepositoryBlockReason
    {
        None = 0,

        /// <summary>병합·리베이스 등이 끝나지 않았다. 작업 트리가 중간 상태다.</summary>
        OperationInProgress = 1,

        /// <summary>어느 브랜치도 가리키지 않는다. 커밋해도 어디에도 남지 않는다.</summary>
        DetachedHead = 2,

        /// <summary>매핑이 고정한 브랜치와 다르다.</summary>
        BranchMismatch = 3,

        /// <summary>
        /// 배포·감사 클론에 커밋되지 않은 변경이 있다. 비교 기준이 브랜치가 아니게 된다.
        /// 개발 클론(write)에서는 정상 상태이므로 발동하지 않는다.
        /// </summary>
        WorkingTreeDirty = 4
    }

    /// <summary>
    /// 저장소를 열었을 때의 상태 한 벌. UI는 <see cref="BlockReason"/>만 보고 화면을 덮는다.
    /// </summary>
    public class RepositoryState
    {
        /// <summary>현재 브랜치 이름. detached이면 null이다.</summary>
        public string? CurrentBranch { get; set; }

        public bool IsDetached { get; set; }

        /// <summary>진행 중인 작업 이름(<c>Merge</c> 등). 없으면 null이다.</summary>
        public string? PendingOperation { get; set; }

        public RepositoryBlockReason BlockReason { get; set; }

        /// <summary>차단되지 않았으면 null. 그 외에는 사용자에게 그대로 보일 한국어 사유다.</summary>
        public string? BlockMessage { get; set; }
    }
}
