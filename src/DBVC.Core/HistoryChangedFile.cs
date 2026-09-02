using System.Collections.Generic;

namespace DBVC.Core
{
    public enum HistoryChangedFileState
    {
        Added,
        Modified,
        Deleted
    }

    public class HistoryChangedFile
    {
        public HistoryChangedFileState State { get; set; }
        public string RelativePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// 커밋 하나를 화면에 그리는 데 필요한 정보를 한 번의 저장소 열기로 모아 담는다.
    ///
    /// 목록과 본문을 한 타입에 둔 이유는 호출 횟수다. 나누면 커밋을 고를 때마다
    /// Repository를 두세 번 열게 되고, 그 비용이 UI 스레드에서 난다.
    /// </summary>
    public class CommitDetail
    {
        /// <summary>표시 상한을 넘어 <see cref="ChangedFiles"/>가 잘렸다.</summary>
        public bool IsTruncated { get; set; }

        /// <summary>자르기 전의 전체 변경 파일 수. 안내 문구가 이 값을 쓴다.</summary>
        public int TotalChangedFileCount { get; set; }

        public IReadOnlyList<HistoryChangedFile> ChangedFiles { get; set; } = new List<HistoryChangedFile>();

        /// <summary>
        /// 부모 커밋 시점의 파일 내용. 부모가 없는 최초 커밋이면 빈 문자열이다.
        /// 조회 경로를 주지 않았거나 그 트리에 파일이 없으면 <c>null</c>이다.
        /// </summary>
        public string? OldText { get; set; }

        /// <summary>이 커밋 시점의 파일 내용. 삭제된 파일이거나 조회 경로를 주지 않았으면 <c>null</c>이다.</summary>
        public string? NewText { get; set; }
    }
}
