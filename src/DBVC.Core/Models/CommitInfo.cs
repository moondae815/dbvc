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
        public string Message { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public DateTimeOffset Date { get; set; }
    }
}
