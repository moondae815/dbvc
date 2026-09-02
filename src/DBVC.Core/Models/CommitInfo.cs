using System;

namespace DBVC.Core.Models
{
    /// <summary>
    /// 객체 이력(Feature 7) 표시에 필요한 커밋 요약 정보.
    /// </summary>
    public class CommitInfo
    {
        public string Sha { get; set; } = string.Empty;
        public string? ParentSha { get; set; }

        /// <summary>
        /// 부모 커밋 수. 2 이상이면 병합 커밋이다.
        /// 화면이 이 값으로 병합 표시를 내는데, 파일 목록과 Diff는 첫 부모 기준이라
        /// 표시가 없으면 사용자가 상대 브랜치에서 들어온 변경을 이 커밋이 만든 것으로 읽는다.
        /// </summary>
        public int ParentCount { get; set; }

        public string Message { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public DateTimeOffset Date { get; set; }
    }
}
