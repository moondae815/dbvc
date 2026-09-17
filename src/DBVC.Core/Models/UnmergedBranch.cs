using System;

namespace DBVC.Core.Models
{
    /// <summary>고정 브랜치에 아직 병합되지 않은 원격 브랜치 한 줄.</summary>
    public class UnmergedBranch
    {
        /// <summary>remote 접두사를 뗀 이름(origin/PROJ-1 → PROJ-1).</summary>
        public string Name { get; set; } = string.Empty;

        public string LastCommitAuthor { get; set; } = string.Empty;

        public DateTimeOffset LastCommitTime { get; set; }

        /// <summary>목적지에 없는 커밋 수.</summary>
        public int CommitCount { get; set; }

        /// <summary>
        /// 끝 커밋이 원격 develop에 들어 있는가. 목적지가 master일 때만 값이 있다.
        /// 막는 데 쓰지 않는다 - hotfix는 테스트를 건너뛰는 것이 정상이다.
        /// </summary>
        public bool? IsInDevelop { get; set; }
    }
}
