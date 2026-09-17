using System;
using System.Collections.Generic;

namespace DBVC.Core.Models
{
    public enum MergeOutcomeKind
    {
        /// <summary>병합 커밋을 만들고 원격에 올렸다.</summary>
        Merged,

        /// <summary>원본 끝이 이미 목적지에 들어 있다.</summary>
        AlreadyMerged,

        /// <summary>충돌로 병합하지 않았다. 로컬은 시작 전 상태다.</summary>
        Conflicts,

        /// <summary>원격이 거부했다. 로컬은 병합 전으로 되돌렸다.</summary>
        PushRejected,

        /// <summary>로컬에 원격에 없는 커밋이 있어 시작하지 않았다.</summary>
        LocalAhead,

        /// <summary>입구 검사에 걸렸다. Message에 사유가 있다.</summary>
        Refused
    }

    /// <summary>
    /// 예상할 수 있는 결과는 값으로 돌려준다. 통신·인증 실패만 예외다 - PushChanges의
    /// NoUpstream과 같은 기준이다.
    /// </summary>
    public class MergeOutcome
    {
        public MergeOutcomeKind Kind { get; set; }

        /// <summary>한국어. 화면이 그대로 띄운다. Merged이면 null.</summary>
        public string? Message { get; set; }

        /// <summary>Conflicts면 충돌 파일, Merged면 바뀐 파일. 그 외에는 빈 목록.</summary>
        public IReadOnlyList<string> Paths { get; set; } = Array.Empty<string>();

        public static MergeOutcome Of(MergeOutcomeKind kind, string? message = null, IReadOnlyList<string>? paths = null) =>
            new MergeOutcome { Kind = kind, Message = message, Paths = paths ?? Array.Empty<string>() };
    }
}
