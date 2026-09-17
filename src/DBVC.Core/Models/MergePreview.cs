using System;
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    /// <summary>작업 트리를 건드리지 않고 계산한 병합 결과.</summary>
    public class MergePreview
    {
        /// <summary>목적지 끝 → 병합 결과 사이에 바뀌는 파일. '/' 구분.</summary>
        public IReadOnlyList<string> ChangedPaths { get; set; } = Array.Empty<string>();

        /// <summary>비어 있지 않으면 병합할 수 없다.</summary>
        public IReadOnlyList<string> ConflictPaths { get; set; } = Array.Empty<string>();

        /// <summary>목적지가 master일 때만 채워진다.</summary>
        public IReadOnlyList<PromotionLeak> Leaks { get; set; } = Array.Empty<PromotionLeak>();

        public bool AlreadyMerged { get; set; }

        /// <summary>
        /// 미리보기를 계산한 원본 끝 커밋. 병합이 이것을 다시 넘겨 Fetch 뒤 원본이 움직였으면 거부한다 -
        /// 그 사이 올라온 커밋은 DBA가 바뀌는 파일도 경고 A도 보지 못한 것이다.
        /// </summary>
        public string SourceSha { get; set; } = string.Empty;
    }

    /// <summary>
    /// 운영으로 가는 줄이 아직 운영에 병합되지 않은 다른 브랜치에도 있다. 방향은 모른다 -
    /// 그쪽에서 딸려 왔을 수도, 이쪽 변경이 그쪽에 딸려 갔을 수도 있다.
    /// </summary>
    public class PromotionLeak
    {
        public string Path { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;

        /// <summary>겹친 줄(정규화된 형태), 정렬됨.</summary>
        public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();
    }
}
