using DBVC.Core.Models;

namespace DBVC.Vsix.ViewModels
{
    /// <summary>미병합 브랜치 한 줄의 한국어 표시. Core는 값만 갖고 화면만 문구를 안다.</summary>
    public class UnmergedBranchItemViewModel
    {
        public UnmergedBranchItemViewModel(UnmergedBranch branch)
        {
            Branch = branch;
        }

        public UnmergedBranch Branch { get; }

        public string Name => Branch.Name;
        public string AuthorText => Branch.LastCommitAuthor;
        public string TimeText => Branch.LastCommitTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string CommitCountText => $"{Branch.CommitCount}개";

        /// <summary>목적지가 master가 아니면 빈 문자열이다 - 열 자체가 숨는다.</summary>
        public string InDevelopText =>
            Branch.IsInDevelop == null ? string.Empty : Branch.IsInDevelop.Value ? "반영됨" : "미반영";
    }
}
