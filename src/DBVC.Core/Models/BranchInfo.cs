namespace DBVC.Core.Models
{
    /// <summary>브랜치 목록 한 줄. 화면이 고르게 하는 데 필요한 만큼만 담는다.</summary>
    public class BranchInfo
    {
        /// <summary>사람이 읽는 이름. 원격 전용이면 remote 접두사를 뗀 값이다(origin/PROJ-1 → PROJ-1).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>지금 체크아웃되어 있는 브랜치인가.</summary>
        public bool IsCurrent { get; set; }

        /// <summary>
        /// 로컬에는 없고 원격 추적 참조로만 있는가. 고르면 로컬 브랜치를 만들어 붙는다.
        /// 남이 만든 티켓 브랜치에 합류하는 경로다.
        /// </summary>
        public bool IsRemoteOnly { get; set; }
    }
}
